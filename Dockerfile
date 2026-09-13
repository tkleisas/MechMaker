# MechMaker headless — engine + HIL + MCP server (no GUI).
#
# Base is Ubuntu 22.04 (jammy) because shimat's OpenCvSharp Linux extern is
# linked against jammy-era natives (tesseract 4, gtk2, ffmpeg 4.4, OpenEXR 2.5)
# — on newer distros those sonames don't exist and ffmpeg's versioned symbols
# can't be symlinked. .NET 10 comes from the official install script (supported
# on jammy's glibc).
#
# The test stage is the portability gate: physics (MuJoCo), engine, server and
# the OpenCV vision path all run on linux-x64 inside the container. The OCR
# tests skip: the Tesseract .NET wrapper's Linux natives are not packaged (the
# NuGet ships Windows-only DLLs and jammy's tesseract is 4.1 where the wrapper
# wants 5.x) — MECHMAKER_TESSDATA points at an empty dir so TextDetector's
# documented skip path applies. A source-built libtesseract 5 is the follow-up.
#
#   docker build --target test -t mechmaker:test .   # the portability gate
#   docker build -t mechmaker .                      # MCP server over stdio
#   docker run --rm mechmaker
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

COPY src/ src/
COPY tests/ tests/
COPY catalog/ catalog/
COPY examples/ examples/
COPY scripts/ scripts/
COPY schema/ schema/
COPY tessdata/ tessdata/

# The MCP server, published for the runtime stage.
RUN dotnet publish src/MechMaker.Server/MechMaker.Server.csproj -c Release -o /publish

# OpenCvSharp's Linux runtime package ships libOpenCvSharpExtern.so without build
# targets (nothing copies it to the output); put it on the loader path.
FROM build AS loader
RUN dotnet restore src/MechMaker.Hil/MechMaker.Hil.csproj \
    && cp "$HOME"/.nuget/packages/opencvsharp4.runtime.linux-x64/*/runtimes/linux-x64/native/libOpenCvSharpExtern.so /usr/local/lib/ \
    && ldconfig

# Test stage: the full suite, all four projects, is the image's health gate.
# OCR skips (no Tesseract 5 natives for Linux yet — see header).
FROM loader AS test
RUN mkdir -p /tmp/no-tessdata
ENV MECHMAKER_TESSDATA=/tmp/no-tessdata
RUN dotnet test tests/MechMaker.Core.Tests/MechMaker.Core.Tests.csproj && \
    dotnet test tests/MechMaker.Engine.Tests/MechMaker.Engine.Tests.csproj && \
    dotnet test tests/MechMaker.Hil.Tests/MechMaker.Hil.Tests.csproj && \
    dotnet test tests/MechMaker.Server.Tests/MechMaker.Server.Tests.csproj

# Runtime stage: the MCP server (stdio) plus the data the tools read live.
FROM build AS runtime
WORKDIR /repo
ENV MECHMAKER_TESSDATA=/tmp/no-tessdata
COPY --from=build /repo/catalog/ catalog/
COPY --from=build /repo/examples/ examples/
COPY --from=build /repo/scripts/ scripts/
COPY --from=build /repo/schema/ schema/
COPY --from=build /publish/ app/
# The workspace resolves the catalog by walking up from the working directory:
# /repo has it, the app DLL sits in app/.
ENTRYPOINT ["dotnet", "app/MechMaker.Server.dll"]
