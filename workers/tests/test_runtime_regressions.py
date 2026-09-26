"""Host-independent regression checks for code recognition and prompt class mapping."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

WORKERS = Path(__file__).resolve().parents[1]


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


decoder = load_module("vw_decode_code", WORKERS / "paddleocr-vl" / "decode_code.py")
yoloe = load_module("vw_yoloe_worker", WORKERS / "yoloe" / "worker.py")


class DecoderTests(unittest.TestCase):
    def test_barcode_prefers_typed_api_and_returns_text(self):
        detector = SimpleNamespace(detectAndDecodeWithType=lambda _: (True, ["5901234123457"], ["EAN_13"], object()))
        cv2 = SimpleNamespace(barcode=SimpleNamespace(BarcodeDetector=lambda: detector))
        with patch.dict(sys.modules, {"cv2": cv2}):
            self.assertEqual(["5901234123457"], decoder.decode_barcode(object()))

    def test_barcode_inherited_api_uses_first_return_value(self):
        detector = SimpleNamespace(detectAndDecode=lambda _: ("12345678", object(), object()))
        cv2 = SimpleNamespace(barcode=SimpleNamespace(BarcodeDetector=lambda: detector))
        with patch.dict(sys.modules, {"cv2": cv2}):
            self.assertEqual(["12345678"], decoder.decode_barcode(object()))

    def test_barcode_legacy_api_remains_supported(self):
        detector = SimpleNamespace(detectAndDecode=lambda _: (True, ["12345678"], ["EAN_8"], object()))
        cv2 = SimpleNamespace(barcode=SimpleNamespace(BarcodeDetector=lambda: detector))
        with patch.dict(sys.modules, {"cv2": cv2}):
            self.assertEqual(["12345678"], decoder.decode_barcode(object()))

    def test_qr_falls_back_when_multi_finds_nothing(self):
        detector = SimpleNamespace(detectAndDecodeMulti=lambda _: (False, [], None, None),
                                   detectAndDecode=lambda _: ("single-code", None, None))
        cv2 = SimpleNamespace(QRCodeDetector=lambda: detector, error=RuntimeError)
        with patch.dict(sys.modules, {"cv2": cv2}):
            self.assertEqual(["single-code"], decoder.decode_qr(object()))

    def test_real_qr_from_unicode_path(self):
        import cv2
        code = cv2.QRCodeEncoder_create().encode("VisionWorkbench-activation-regression")
        code = cv2.resize(code, None, fx=12, fy=12, interpolation=cv2.INTER_NEAREST)
        with tempfile.TemporaryDirectory(prefix="vw-code-test-") as directory:
            path = Path(directory) / "中文路径二维码.png"
            ok, encoded = cv2.imencode(".png", code)
            self.assertTrue(ok)
            path.write_bytes(encoded.tobytes())
            result = subprocess.run([sys.executable, str(WORKERS / "paddleocr-vl" / "decode_code.py"),
                                     "qrcode", str(path), directory], capture_output=True, text=True,
                                    encoding="utf-8", timeout=30)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(["VisionWorkbench-activation-regression"], json.loads(result.stdout)["values"])


class YoloPromptTests(unittest.TestCase):
    def test_sparse_dataset_classes_are_mapped_to_dense_model_classes(self):
        prompts = [{"classId": 5, "className": "part"}, {"classId": 17, "className": "defect"}]
        ids, mapping = yoloe.prompt_class_mapping(prompts)
        self.assertEqual([0, 1], ids)
        self.assertEqual("defect", mapping[1]["className"])

    def test_repeated_prompts_for_one_class_share_the_model_class(self):
        prompts = [{"classId": 17, "className": "defect"}, {"classId": 17, "className": "defect"},
                   {"classId": 2, "className": "part"}]
        ids, mapping = yoloe.prompt_class_mapping(prompts)
        self.assertEqual([0, 0, 1], ids)
        self.assertEqual("part", mapping[1]["className"])


if __name__ == "__main__":
    unittest.main()
