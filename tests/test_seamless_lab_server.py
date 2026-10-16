import importlib.util
from pathlib import Path
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "deploy/seamless_lab/lab_server.py"
SPEC = importlib.util.spec_from_file_location("lab_server", SCRIPT)
lab_server = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(lab_server)


class SeamlessLabServerTests(unittest.TestCase):
    def test_template_rewrites_only_four_shadnet_routes_and_restart_is_safe(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as directory:
            template = Path(directory) / "ss_info_template.xml"
            template.write_text("http://{YOUR SHADNET SERVER}:31315\n" * 4 +
                                "http://127.0.0.1:20443\n", encoding="utf-8")
            lab_server.configure(template, "192.168.31.226")
            lab_server.configure(template, "192.168.31.226")
            content = template.read_text(encoding="utf-8")
            self.assertEqual(content.count("http://192.168.31.226:32315"), 4)
            self.assertIn("http://127.0.0.1:20443", content)

    def test_template_rejects_unexpected_source_or_invalid_address(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as directory:
            template = Path(directory) / "ss_info_template.xml"
            template.write_text("unexpected", encoding="utf-8")
            with self.assertRaises(RuntimeError):
                lab_server.configure(template, "192.168.31.226")
            with self.assertRaises(ValueError):
                lab_server.configure(template, "not-an-ip")


if __name__ == "__main__":
    unittest.main()
