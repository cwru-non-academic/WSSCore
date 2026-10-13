"""Write execution evidence to the Actions summary using only the standard library."""

import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET


def main():
    steps = json.loads(os.environ["REPORT_STEPS"])
    lines = [
        "## " + os.environ["REPORT_TITLE"],
        "",
        os.environ["REPORT_PURPOSE"],
        "",
        "### Actual step results",
        "",
        "| Step (see named job log for commands) | Outcome |",
        "| --- | --- |",
    ]
    for name, result in steps.items():
        lines.append(f"| {name.replace('_', ' ')} | {result['outcome']} |")
    lines.extend(["", "### NUnit / TRX results", ""])
    results = sorted((Path(os.environ["RUNNER_TEMP"]) / "test-results").glob("**/*.trx"))
    if not results:
        lines.append("No TRX results available. Consumer/packaging tools use exit codes, not NUnit case counts. Missing results are not evidence of passing tests; see step outcomes above.")
    for path in results:
        try:
            root = ET.parse(path).getroot()
            counters = root.find(".//{*}ResultSummary/{*}Counters")
            if counters is None:
                raise ValueError("TRX has no counters")
            count = counters.attrib
            lines.append(
                f"- **{path.name}**: runner total {count['total']}, "
                f"executed {count['executed']}, passed {count['passed']}, "
                f"failed {count['failed']}, not executed/skipped {count.get('notExecuted', 'not reported')}."
            )
        except (ET.ParseError, ValueError, KeyError) as error:
            lines.append(f"- **{path.name}**: results could not be summarized ({error}); inspect the downloadable TRX/log. No pass counts inferred.")
    if "bluez_controller_classification" in steps:
        lines.extend(["", "### Linux controller classification", ""])
        if os.environ.get("WSS_BLE_SKIP_NATIVE_PROBE") == "1":
            lines.append("Controller/management interface classified unavailable: native controller probe NOT EXECUTED. Managed dependency checks remain mandatory; their actual outcomes are above.")
        else:
            lines.append("No controller skip was recorded. This alone does not establish probe success; see the classification and consumer step outcomes/logs.")
    lines.extend([
        "",
        "### Coverage boundaries",
        "",
        "Consumer smoke checks do not establish physical Serial communication, BLE discovery/GATT reception, stimulation, or Unity runtime compatibility. Automated jobs do not connect peripherals or write physical stimulation. Source-project NUnit tests and extracted-artifact consumers remain separate layers.",
        "",
        "TRX artifacts (when produced) retain individual NUnit cases, including failures. Step failures are not suppressed; skipped/unexecuted steps are not passing checks.",
        "",
        "Permanent suite descriptions and test-placement policy: [tests/README.md](" +
        os.environ["GITHUB_SERVER_URL"] + "/" + os.environ["GITHUB_REPOSITORY"] +
        "/blob/" + os.environ["GITHUB_SHA"] + "/tests/README.md).",
        "",
    ])
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
        summary.write("\n".join(lines))


if __name__ == "__main__":
    main()
