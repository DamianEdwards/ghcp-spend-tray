import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SUBMISSION = "11111111-2222-3333-4444-555555555555"
MOCK = r'''
xcrun() {
    printf '%s\n' "$*" >> calls.txt
    if [ "$1" != notarytool ]; then return 1; fi
    case "$2" in
        submit)
            printf '%s\n' "$SUBMIT_RESPONSE"
            return "$SUBMIT_EXIT"
            ;;
        wait)
            if [ ! -f artifacts/macos-notarization/app-submission.json ]; then return 99; fi
            printf '%s\n' "$WAIT_RESPONSE"
            return "$WAIT_EXIT"
            ;;
        *) return 1 ;;
    esac
}
export -f xcrun
bash "$1" synthetic.zip app
'''


class NotarizationTests(unittest.TestCase):
    def execute(self, root, *, status="Accepted", wait_exit=0, submit_exit=0, submission=SUBMISSION):
        env = dict(os.environ, GHCP_SIGNING_TEMP=str(root), MACOS_NOTARY_KEY_ID="synthetic-id",
                   MACOS_NOTARY_ISSUER="synthetic-issuer", SUBMIT_EXIT=str(submit_exit), WAIT_EXIT=str(wait_exit),
                   SUBMIT_RESPONSE=json.dumps(dict(id=submission)), WAIT_RESPONSE=json.dumps(dict(status=status)))
        return subprocess.run(["bash", "-c", MOCK, "test", str(ROOT / "tools/macos/notarize.sh")],
                              cwd=root, env=env, capture_output=True, text=True)

    def test_success_waits_on_saved_submission(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result = self.execute(root)
            self.assertEqual(result.returncode, 0, result.stderr)
            calls = root.joinpath("calls.txt").read_text().splitlines()
            self.assertEqual(len(calls), 2)
            self.assertNotIn("--wait", calls[0])
            self.assertIn("notarytool wait " + SUBMISSION, calls[1])
            self.assertIn("--timeout 60m", calls[1])
            receipt = root / "artifacts/macos-notarization/app-submission.json"
            self.assertEqual(json.loads(receipt.read_text())["id"], SUBMISSION)
            self.assertIn("Notarization accepted.", result.stdout)

    def test_timeout_preserves_id_and_never_reuploads(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result = self.execute(root, status="In Progress", wait_exit=1)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("does not cancel", result.stderr)
            self.assertIn(SUBMISSION, root.joinpath("artifacts/macos-notarization/app-submission.json").read_text())
            retry = self.execute(root)
            self.assertNotEqual(retry.returncode, 0)
            self.assertIn("already recorded", retry.stderr)
            self.assertEqual(len(root.joinpath("calls.txt").read_text().splitlines()), 2)

    def test_invalid_or_unknown_status_is_not_success(self):
        for status in ("Invalid", "Rejected", "In Progress", "Unknown"):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as directory:
                result = self.execute(Path(directory), status=status)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("not accepted", result.stderr)

    def test_upload_failure_or_bad_id_never_waits(self):
        for options in (dict(submit_exit=1), dict(submission="not-a-submission-id")):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                result = self.execute(root, **options)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(root.joinpath("calls.txt").read_text().splitlines()), 1)

    def test_workflow_budget_and_receipt_retention(self):
        workflow = (ROOT / ".github/workflows/release.yml").read_text().split("\n  macos:\n")[1]
        self.assertIn("timeout-minutes: 180", workflow)
        self.assertIn("always() && steps.sign.outcome", workflow)
        self.assertIn("path: artifacts/macos-notarization/", workflow)
        script = (ROOT / "tools/macos/sign-package.sh").read_text()
        self.assertIn('notarize.sh "$temporary/app.zip" app', script)
        self.assertIn('notarize.sh "$dmg" dmg', script)
