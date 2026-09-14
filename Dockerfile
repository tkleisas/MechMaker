# MechMaker headless — engine + HIL + MCP server (no GUI).
#
# Base is Ubuntu 22.04 (jammy) because shimat's OpenCvSharp Linux extern is
# linked against jammy-era natives (tesseract 4, gtk2, ffmpeg 4.4, OpenEXR 2.5)
# — on newer distros those sonames don't exist and ffmpeg's versioned symbols
# can't be symlinked. .NET 10 comes from the official install script (supported
# on jammy's glibc). Tesseract 5 is built from source (jammy ships 4.1; the
# OCR wrapper wants the 5.x C API) against jammy's leptonica 1.82 — the same
# version the wrapper's Windows package carries — so both tesseract sonames
# coexist: the extern's .so.4 and the wrapper's .so.5.
#
# The test stage is the portability gate: the full suite — physics (MuJoCo),
# engine, server, OpenCV vision AND real OCR — runs on linux-x64.
#
#   docker build --target test -t mechmaker:test .   # the portability gate
#   docker build -t mechmaker .                      # MCP server
#   docker run --rm mechmaker                        # stdio (agent default)
#   docker run --rm -p 8080:8080 mechmaker --http    # streamable HTTP at /mcp
#
# The live adb transport stays host-side: point ANDROID_ADB at an adb binary or
# connect to a host emulator over TCP when running with --network.

FROM ubuntu:22.04 AS build
ENV DEBIAN_FRONTEND=noninteractive
WORKDIR /repo

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
        libgtk2.0-0 libdc1394-25 libtiff5 libopenexr25 \
        libavcodec58 libavformat58 libavutil56 libswscale5 \
        libtesseract4 \
    && rm -rf /var/lib/apt/lists/*

# .NET 10 SDK (official tarball; jammy is a supported .NET 10 platform).
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet

# Tesseract 5 from source against jammy's leptonica 1.82 (the wrapper's
# expected version). The C API is stable across 5.x.
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential cmake pkg-config \
        libarchive-dev libleptonica-dev \
    && curl -fsSL https://github.com/tesseract-ocr/tesseract/archive/refs/tags/5.3.4.tar.gz -o /tmp/tess.tar.gz \
    && tar xzf /tmp/tess.tar.gz -C /tmp \
    && cmake -S /tmp/tesseract-5.3.4 -B /tmp/tess-build -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=ON -DBUILD_TRAINING_TOOLS=OFF \
    && cmake --build /tmp/tess-build -j "$(nproc)" \
    && cmake --install /tmp/tess-build \
    && rm -rf /tmp/tess* \
    && ln -sf libtesseract.so.5.3.4 /usr/local/lib/libtesseract.so.5 \
    && ldconfig \
    && ln -s /usr/local/lib/libtesseract.so.5 /usr/lib/x86_64-linux-gnu/libtesseract50.so \
    && ln -s /usr/lib/x86_64-linux-gnu/liblept.so.5 /usr/lib/x86_64-linux-gnu/libleptonica-1.82.0.so

COPY src/ src/
COPY tests/ tests/
COPY catalog/ catalog/
COPY examples/ examples/
COPY scripts/ scripts/
COPY schema/ schema/

# tessdata is a fetched artifact (gitignored); the OCR tests need it, so fetch
# the same model tools/fetch_tessdata.ps1 uses.
RUN mkdir -p tessdata \
    && curl -fsSL https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata -o tessdata/eng.traineddata

# The MCP server, published for the runtime stage.
RUN dotnet publish src/MechMaker.Server/MechMaker.Server.csproj -c Release -o /publish

# OpenCvSharp's Linux runtime package ships libOpenCvSharpExtern.so without build
# targets (nothing copies it to the output); put it on the loader path.
FROM build AS loader
RUN dotnet restore src/MechMaker.Hil/MechMaker.Hil.csproj \
    && cp "$HOME"/.nuget/packages/opencvsharp4.runtime.linux-x64/*/runtimes/linux-x64/native/libOpenCvSharpExtern.so /usr/local/lib/ \
    && ldconfig

# Test stage: the full suite, all four projects, is the image's health gate —
# including the OCR tests against the source-built tesseract 5.
FROM loader AS test
# The Tesseract wrapper's InteropDotNet loader probes app-relative paths only
# ({app}/x64/{name}, {app}/{name}); alias the built natives next to each test
# binary. libdl: merged into glibc ≥2.34, the runtime keeps only libdl.so.2 —
# the wrapper's P/Invoke wants the unversioned name. One build first, so the
# bins exist.
RUN ln -sf /lib/x86_64-linux-gnu/libdl.so.2 /usr/lib/x86_64-linux-gnu/libdl.so \
    && dotnet build tests/MechMaker.Hil.Tests/MechMaker.Hil.Tests.csproj \
    && dotnet build tests/MechMaker.Server.Tests/MechMaker.Server.Tests.csproj \
    && for BIN in /repo/tests/MechMaker.Hil.Tests/bin/Debug/net10.0 \
                  /repo/tests/MechMaker.Server.Tests/bin/Debug/net10.0; do \
        mkdir -p "$BIN/x64"; \
        ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5 "$BIN/x64/libleptonica-1.82.0.so"; \
        ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5 "$BIN/libleptonica-1.82.0.so"; \
        ln -sf /usr/local/lib/libtesseract.so.5 "$BIN/x64/libtesseract50.so"; \
        ln -sf /usr/local/lib/libtesseract.so.5 "$BIN/libtesseract50.so"; \
    done
RUN dotnet test tests/MechMaker.Core.Tests/MechMaker.Core.Tests.csproj && \
    dotnet test tests/MechMaker.Engine.Tests/MechMaker.Engine.Tests.csproj && \
    dotnet test tests/MechMaker.Hil.Tests/MechMaker.Hil.Tests.csproj && \
    dotnet test tests/MechMaker.Server.Tests/MechMaker.Server.Tests.csproj

# Runtime stage: the MCP server (stdio) plus the data the tools read live.
FROM build AS runtime
WORKDIR /repo
COPY --from=build /repo/catalog/ catalog/
COPY --from=build /repo/examples/ examples/
COPY --from=build /repo/scripts/ scripts/
COPY --from=build /repo/schema/ schema/
COPY --from=build /repo/tessdata/ tessdata/
COPY --from=build /publish/ app/
# Same app-relative aliases for the wrapper's loader (agents can OCR in-container).
RUN ln -sf /lib/x86_64-linux-gnu/libdl.so.2 /usr/lib/x86_64-linux-gnu/libdl.so \
    && mkdir -p app/x64 \
    && ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5 app/x64/libleptonica-1.82.0.so \
    && ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5 app/libleptonica-1.82.0.so \
    && ln -sf /usr/local/lib/libtesseract.so.5 app/x64/libtesseract50.so \
    && ln -sf /usr/local/lib/libtesseract.so.5 app/libtesseract50.so
# The workspace resolves the catalog by walking up from the working directory:
# /repo has it, the app DLL sits in app/.
EXPOSE 8080
ENTRYPOINT ["dotnet", "app/MechMaker.Server.dll"]