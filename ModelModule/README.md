# Modeled stimulation

`StimWithMode(channel, input)` applies the selected P/PD model, passes its clamped result to calibration, and sends the resulting channel state through Core. Global channels 1–3 belong to the first WSS, 4–6 to the second, and 7–9 to the third. Model history has one entry per calibrated channel.

Model gains and offsets are read only from `modelConfig.json`'s `constants` object:

```json
{
  "calib": { "mode": "P" },
  "constants": {
    "PModeProportional": 1.0,
    "PModeOffset": 0.0,
    "PDModeProportional": 0.5,
    "PDModeDerivative": 0.2,
    "PDModeOffset": 0.0
  }
}
```

A failed nested lookup uses the corresponding built-in value shown above. Top-level keys with those names are ignored, including when the nested key is absent or invalid. The existing configuration conversion rules and model equations are preserved. Use `GetModelConfigController().SetModelParam("constants.PModeOffset", value)` to change an offset through the public controller.

Ordinary calls reuse each channel's magnitude and derivative history. Reloading model or stimulation-parameter files does not reset history. Successful synchronous `Initialize()` resets history and timing, including the original first-sample derivative and minimum-dt behavior. Changing device count after stack construction requires a new complete stack; initialization rejects a topology that differs from the parameter controller capacity. This does not provide live topology reconfiguration.

Zero raw input can produce nonzero calculated output because of offsets and PD history. Zero calculated output still follows the calibration layer's existing PW/PA rules.

## Behavior change

Correcting channel capacity allows previously suppressed modeled channels to emit their configured stimulation. Reading nested constants makes previously ignored configured gains and offsets effective. Existing top-level overrides no longer affect modeled output. These changes can alter output on channels that already worked as well as activate previously suppressed channels. No calibration file migration or physical-device operation is performed by the fix.

Core owns detailed JSON, model-value and history tests. Integration suites invoke their public APIs with Core-owned layered scenarios and validate decoded commands through the emulator. Emulator results do not measure physical stimulation or establish hardware/BLE behavior.
