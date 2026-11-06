# Modeled stimulation

`StimWithMode(channel, input)` applies the selected P/PD model, passes its clamped result to calibration, and sends the resulting channel state through Core. Global channels 1â€“3 belong to the first WSS, 4â€“6 to the second, and 7â€“9 to the third. Model history has one entry per calibrated channel.

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

Ordinary calls reuse each channel's magnitude and derivative history. Reloading model or calibration values without restarting Core preserves history. A coordinated reload that changes Core starts a new session. Successful synchronous `Initialize()` resets history and timing, including the original first-sample derivative and minimum-dt behavior. Direct initialization rejects a topology that differs from the parameter controller capacity. Coordinated reloads install matching Core and parameter capacities through the reset path; they do not change topology within a running session.

Initialization delegates once to Core, preserving Core's shutdown-before-configuration-load order. Model topology validation and complete replacement-history preparation follow that call. A failure invokes the existing shutdown API and retains the old model history; it does not resume a session. The original exception and stack are rethrown. If shutdown also fails, the cleanup exception is attached under `exception.Data["Wss.ModelInitialization.ShutdownException"]`; a stopped session cannot be guaranteed in that case.

Zero raw input can produce nonzero calculated output because of offsets and PD history. Zero calculated output still follows the calibration layer's existing PW/PA rules.
