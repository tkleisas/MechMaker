# PinApp — minimal Android PIN screen for the HIL POC

A dependency-free Android `Activity` with a 3 x 4 keypad. It logs
`PIN_ACCEPTED=<pin>`, writes `files/pin.txt`, and shows `APPROVED` after four
digits and OK. Built without Gradle so it is easy to reproduce on a workstation.

## Prerequisites

- JDK 17 (`JAVA_HOME`)
- Android SDK (`ANDROID_SDK_ROOT`)
- `build-tools/34.0.0`
- `platforms/android-34/android.jar`
- API 29 emulator: `system-images;android-29;default;x86_64`

## Build and install

```bash
export JAVA_HOME=/path/to/jdk-17
export ANDROID_SDK_ROOT=$HOME/Android/Sdk

./build.sh
./build.sh --install emulator-5554
```

The unsigned APK is assembled with `aapt2`, `javac`, `d8`, `zip`, and signed
with a local debug keystore (generated on first run, gitignored).

## Emulator

```bash
sdkmanager "system-images;android-29;default;x86_64"
avdmanager create avd -n pin29 -k "system-images;android-29;default;x86_64" -d pixel_2
emulator -avd pin29 -no-window -no-audio -no-boot-anim \
  -gpu swiftshader_indirect -no-snapshot -accel on

adb shell wm size 720x1440
adb shell wm density 320
./build.sh --install emulator-5554
```

## Verify the app alone

```bash
adb shell input tap 155 539   # key 1 (720 x 1440 coordinates)
adb shell run-as com.tkleisas.pinapp cat files/pin.txt
adb logcat -d -s PinApp
```
