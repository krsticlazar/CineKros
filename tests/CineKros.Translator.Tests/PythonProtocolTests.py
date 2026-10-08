import importlib.util
import unittest
from pathlib import Path


RUNNER = Path(__file__).resolve().parents[2] / "src" / "etl" / "CineKros.Translator" / "python" / "translate.py"
SPEC = importlib.util.spec_from_file_location("cinekros_translate", RUNNER)
TRANSLATE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(TRANSLATE)


class PythonProtocolTests(unittest.TestCase):
    def test_selected_exact_tag_is_valid_without_phase2_smoke_whitelist(self):
        self.assertEqual(
            ("unseen selected tag", "unseen selected tag", TRANSLATE.TARGET, "marian-do-sample-false-beam4-maxnew32-v1"),
            TRANSLATE._validate_job({
                "en": "unseen selected tag",
                "text": "unseen selected tag",
                "target": TRANSLATE.TARGET,
                "settingsId": "marian-do-sample-false-beam4-maxnew32-v1",
            }),
        )

    def test_protocol_accepts_418_unique_tags_then_rejects_the_419th(self):
        seen = set()
        for index in range(418):
            TRANSLATE._register_job(f"selected tag {index}", seen)
        self.assertEqual(418, len(seen))
        with self.assertRaisesRegex(ValueError, "duplicate_or_over_limit_job"):
            TRANSLATE._register_job("selected tag 418", seen)

    def test_protocol_rejects_duplicate_even_below_cap(self):
        seen = {"selected tag"}
        with self.assertRaisesRegex(ValueError, "duplicate_or_over_limit_job"):
            TRANSLATE._register_job("selected tag", seen)


if __name__ == "__main__":
    unittest.main()
