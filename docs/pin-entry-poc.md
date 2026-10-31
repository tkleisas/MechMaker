# PIN-entry POC

Goal: the physical finger on the DX8000 HIL gantry enters a PIN in the emulated
POS app. Vision finds each keypad key, `tap_at` moves and presses it, and the app
reports the accepted PIN.

## Components

- `examples/dx8000_hil_rig.json` — DX8000 on the screen-centre mount, 40 mm raised
  gantry, two-axis gantry and conductive finger.
- `MechMaker.Hil.FakePinPadTransport` — deterministic 720 x 1440 POS PIN screen
  (`ENTER PIN`, masked PIN, 1..9 / CLR / 0 / OK). It records accepted taps and
  exposes `EnteredPin`.
- `MechMaker.Hil.ScreenDetector` — template matching over the live screencap.
- `PhysicsTapBridge` / `TapAtController` — physical contact dispatch and gantry
  motion; the bridge turns a glass contact into an emulator tap.
- `tests/MechMaker.Server.Tests/PinEntryPocTests.cs` — the end-to-end proof.

## Flow

1. Open the DX8000 HIL rig, connect the emulated app, start the run, arm physics
   taps.
2. For each digit, match an independent key template against the live screencap.
3. `tap_at(match.Fx, match.Fy)` moves the gantry, presses, retracts, and the bridge
   dispatches the measured contact to the app.
4. Tap `OK`; assert the app accepted `1234`.

The test uses template matching for the keypad; `TextDetector.ReadWords` is
available for text fields and key labels when OCR is preferable.

## Run it

```bash
scripts/test-linux.sh
# or
dotnet test tests/MechMaker.Server.Tests/MechMaker.Server.Tests.csproj \
  --filter FullyQualifiedName~PinEntryPocTests
```

`scripts/test-linux.sh` runs the Ubuntu 22.04 test container because the
prebuilt OpenCvSharp/Tesseract natives are jammy-specific.

## Real Android emulator

The fake transport is the deterministic CI stand-in. To run against a real
emulator, install a test APK with the same keypad screen and connect with
`HilConnect("adb", ...)`; the HIL session, template matching, `tap_at`, and
physics bridge are unchanged. The fake and adb transports both work in screen
fractions, so the same key templates and tap sequence apply.


## Visual run

`tools/pinvideo` renders the MechMaker cell and the emulated app at each physical
tap and writes frames plus `steps.json` to `tools/pinvideo/out/` (gitignored).
Run it with the test image so the OpenCV/MuJoCo natives are available:

```bash
# Build the test image once with scripts/test-linux.sh, then:
docker run --rm -v "$PWD:/work" -w /work/tools/pinvideo \
  --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
  mechmaker:test dotnet run -c Release
```

The frame sequence shows the finger over each measured key, the PIN buffer
growing from `----` to `****`, and the final `APPROVED` screen. Compose the GIF,
MP4 and contact sheet with:

```bash
python3 tools/pinvideo/compose.py
```


## Live Android emulator E2E

The `apps/android-pin` app is a real Android APK with the same keypad. Build and
install it (see `apps/android-pin/README.md`), start an API 29 emulator at
720 x 1440, then run:

```bash
cd /path/to/MechMaker
docker run --rm --network host   -v "$PWD:/work"   -v "${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}/platform-tools:/android-tools"   -w /work/tools/pinlive   --user "$(id -u):$(id -g)"   -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget   -e MECHMAKER_TESSDATA=/repo/tessdata   mechmaker:test dotnet run -c Release -- \
  --adb /android-tools/adb --serial emulator-5554 --pin 1234
```

The runner:

1. force-stops and relaunches the app,
2. reads the keypad with OCR (falling back to `uiautomator dump`),
3. calls `tap_at` for each digit and OK on the DX8000 HIL rig,
4. verifies `files/pin.txt` and the on-screen `APPROVED` state.

The physical taps are still performed by the simulated finger; only the screen
and input transport are the real Android emulator.

## Live video with the emulator screen on the DX8000

`tools/pinlivevideo` runs the same live E2E and records:

- the DX8000 HIL rig at 30 fps,
- the real Android emulator screenshot, projected onto the DX8000 screen as a
  composited texture,
- the finger/Y-nut close-up.

Run it, then compose with the shared video script:

```bash
docker run --rm --network host \
  -v "$PWD:/work" \
  -v "${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}/platform-tools:/android-tools" \
  -w /work/tools/pinlivevideo \
  --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
  mechmaker:test dotnet run -c Release -- \
  --adb /android-tools/adb --serial emulator-5554 --pin 1234

python3 tools/pinvideo/compose.py tools/pinlivevideo/out
```

The texture is a perspective composite of the emulator capture onto the physical
screen quad, not a mesh material in the renderer. Frames are written to
`tools/pinlivevideo/out/` (gitignored).
