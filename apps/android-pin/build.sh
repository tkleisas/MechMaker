#!/usr/bin/env bash
# Build, sign and (optionally) install the minimal PinApp APK without Gradle.
#
#   ANDROID_SDK_ROOT=/path/to/Sdk ./build.sh
#   ANDROID_SDK_ROOT=/path/to/Sdk ./build.sh --install emulator-5554
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -n "${JAVA_HOME:-}" ]; then
    export PATH="$JAVA_HOME/bin:$PATH"
fi

SDK="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-$HOME/Android/Sdk}}"
BUILD_TOOLS="${BUILD_TOOLS:-$SDK/build-tools/34.0.0}"
PLATFORM="${PLATFORM:-$SDK/platforms/android-34/android.jar}"
ADB="${ADB:-$SDK/platform-tools/adb}"
OUT="$ROOT/build"
JAVA="${JAVA_HOME:+$JAVA_HOME/bin/}java"
JAVAC="${JAVA_HOME:+$JAVA_HOME/bin/}javac"
KEYTOOL="${JAVA_HOME:+$JAVA_HOME/bin/}keytool"

rm -rf "$OUT"
mkdir -p "$OUT/classes" "$OUT/dex"

"$BUILD_TOOLS/aapt2" link \
    -o "$OUT/unsigned.apk" \
    -I "$PLATFORM" \
    --manifest "$ROOT/AndroidManifest.xml"

"$JAVAC" -source 8 -target 8 -classpath "$PLATFORM" \
    -d "$OUT/classes" \
    $(find "$ROOT/src" -name '*.java')

"$BUILD_TOOLS/d8" --lib "$PLATFORM" --min-api 29 \
    --output "$OUT/dex" $(find "$OUT/classes" -name '*.class')

(cd "$OUT" && zip -q -j unsigned.apk dex/classes.dex)

if [ ! -f "$ROOT/debug.keystore" ]; then
    "$KEYTOOL" -genkeypair -keystore "$ROOT/debug.keystore" -storepass android \
        -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 \
        -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
fi

"$BUILD_TOOLS/apksigner" sign --ks "$ROOT/debug.keystore" --ks-pass pass:android \
    --key-pass pass:android --out "$OUT/pinapp.apk" "$OUT/unsigned.apk"
"$BUILD_TOOLS/apksigner" verify "$OUT/pinapp.apk"

echo "built $OUT/pinapp.apk"

if [ "${1:-}" = "--install" ]; then
    serial="${2:-}"
    args=()
    [ -n "$serial" ] && args=(-s "$serial")
    "$ADB" "${args[@]}" install -r "$OUT/pinapp.apk"
    "$ADB" "${args[@]}" shell am force-stop com.tkleisas.pinapp
    "$ADB" "${args[@]}" shell am start -n com.tkleisas.pinapp/.MainActivity
fi
