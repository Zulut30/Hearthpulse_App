"""installer/sign-sums.sh signs SHA256SUMS.txt the way the updater reads it, and refuses a release clients could not check.

Run from the repository root: python -B -I -m unittest discover -s tools/skill_checks -v
Needs bash and OpenSSL 3 (Ed25519 with -rawin): Linux CI, or Git Bash on Windows; skipped otherwise.
"""
import base64
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "installer" / "sign-sums.sh"


def find_bash():
    # On Windows, System32\bash.exe is WSL's launcher and does not see this checkout's paths; prefer Git Bash.
    for candidate in (r"C:\Program Files\Git\bin\bash.exe", shutil.which("bash")):
        if candidate and os.path.isfile(candidate) and "system32" not in candidate.lower():
            return candidate
    return None


BASH = find_bash()


def sh(command, cwd):
    return subprocess.run([BASH, "-c", command], cwd=cwd, capture_output=True, text=True)


def usable():
    if not BASH:
        return False
    with tempfile.TemporaryDirectory() as tmp:
        probe = sh("openssl genpkey -algorithm ed25519 -out k.pem && echo x > m && "
                   "openssl pkeyutl -sign -inkey k.pem -rawin -in m -out s.bin", tmp)
        return probe.returncode == 0


@unittest.skipUnless(usable(), "bash with OpenSSL 3 (Ed25519) is not available")
class SignSumsTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        dist = self.dir / "dist"
        dist.mkdir()
        (dist / "Hearthpulse-Setup-0.15.0-win-x64.exe").write_bytes(b"setup")
        (dist / "SHA256SUMS.txt").write_bytes(b"0" * 64 + b"  Hearthpulse-Setup-0.15.0-win-x64.exe\n")
        for name in ("signing", "other"):
            self.assertEqual(sh(f"openssl genpkey -algorithm ed25519 -out {name}.pem && "
                                f"openssl pkey -in {name}.pem -pubout -out {name}.pub", self.dir).returncode, 0)
        (self.dir / "empty.pub").write_text("# no key yet\n", encoding="utf-8")
        # Rotation: the file may hold several blocks; comments around them are ignored.
        (self.dir / "both.pub").write_text("# old key\n" + (self.dir / "other.pub").read_text() + "# new key\n"
                                           + (self.dir / "signing.pub").read_text(), encoding="utf-8")

    def tearDown(self):
        self._tmp.cleanup()

    def run_script(self, keyfile, secret=None, publishing=False, selftest=False):
        env = []
        if secret:
            env.append(f'UPDATE_SIGNING_KEY="$(cat {secret})"')
        env.append(f"PUBLISHING={'true' if publishing else 'false'}")
        env.append(f"SIGN_SELFTEST={'true' if selftest else 'false'}")
        script = SCRIPT.as_posix()
        return sh(f'{" ".join(env)} bash "{script}" dist {keyfile}', self.dir)

    @property
    def sig(self):
        return self.dir / "dist" / "SHA256SUMS.txt.sig"

    def verify(self, pub):
        raw = base64.b64decode(self.sig.read_text().strip())
        (self.dir / "sig.bin").write_bytes(raw)
        return sh(f"openssl pkeyutl -verify -pubin -inkey {pub} -rawin -in dist/SHA256SUMS.txt -sigfile sig.bin",
                  self.dir).returncode == 0

    def test_the_secret_signs_and_an_embedded_key_verifies(self):
        result = self.run_script("both.pub", secret="signing.pem", publishing=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        text = self.sig.read_text()
        self.assertEqual(len(text.strip()), 88)
        self.assertTrue(text.endswith("\n"))
        self.assertEqual(len(base64.b64decode(text.strip())), 64)
        self.assertTrue(self.verify("signing.pub"))
        self.assertFalse(self.verify("other.pub"))
        self.assertIn("signature verified", result.stdout)

    def test_a_secret_that_is_not_the_embedded_key_stops_the_release(self):
        result = self.run_script("other.pub", secret="signing.pem", publishing=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.sig.exists())

    def test_an_embedded_key_without_the_secret_stops_publishing_but_not_a_trial_run(self):
        self.assertNotEqual(self.run_script("both.pub", publishing=True).returncode, 0)
        trial = self.run_script("both.pub", publishing=False)
        self.assertEqual(trial.returncode, 0, trial.stderr)
        self.assertIn("not signed", trial.stdout)
        self.assertFalse(self.sig.exists())

    def test_no_key_and_no_secret_publishes_unsigned_with_a_warning(self):
        result = self.run_script("empty.pub", publishing=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("not signed", result.stdout)
        self.assertFalse(self.sig.exists())
        self.assertEqual(self.run_script("missing.pub", publishing=True).returncode, 0)

    def test_a_secret_with_no_embedded_key_signs_and_warns(self):
        result = self.run_script("empty.pub", secret="signing.pem", publishing=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("carries no update key", result.stdout)
        self.assertTrue(self.verify("signing.pub"))

    def test_the_selftest_signs_with_a_throwaway_key_and_only_in_trial_runs(self):
        result = self.run_script("both.pub", selftest=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("a changed byte and another key rejected", result.stdout)
        self.assertTrue(self.sig.exists())
        self.assertFalse(self.verify("signing.pub"))
        self.sig.unlink()
        self.assertNotEqual(self.run_script("both.pub", selftest=True, publishing=True).returncode, 0)


if __name__ == "__main__":
    unittest.main()
