#!/usr/bin/env bash
# Run the full Linux suite in the supported Ubuntu 22.04 test container.
#
# The prebuilt OpenCvSharp native is linked against jammy-era sonames
# (libtesseract.so.4, ffmpeg 58, OpenEXR 2.5, libtiff 5). Newer distros do not
# provide those, so local `dotnet test` cannot load the HIL/OCR tests. The
# Dockerfile's `test` stage installs the jammy natives, builds Tesseract 5 for
# the wrapper, and runs all four test projects.
#
#   scripts/test-linux.sh
#   scripts/test-linux.sh --no-cache
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if ! command -v docker >/dev/null 2>&1; then
    echo "error: docker is required for the Linux test stage" >&2
    echo "       install Docker, or run on Ubuntu 22.04 with the native libs" >&2
    exit 127
fi

if ! docker info >/dev/null 2>&1; then
    echo "error: the Docker daemon is not reachable" >&2
    echo "       start Docker and retry (or use 'docker build --target test .')" >&2
    exit 1
fi

IMAGE="${MECHMAKER_TEST_IMAGE:-mechmaker:test}"

echo "Building and running the Linux test stage (image: $IMAGE)…"
docker build --target test -t "$IMAGE" "$@" .

echo
echo "Linux suite passed in $IMAGE."
