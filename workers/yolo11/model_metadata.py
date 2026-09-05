"""Read class metadata embedded in a YOLO/ATU5 model."""
from __future__ import annotations

import contextlib
import io
import json
import sys
from pathlib import Path
from typing import Any


def _names(value: Any) -> list[str] | dict[str, str]:
    if isinstance(value, dict):
        return {str(key): str(name) for key, name in value.items()}
    if isinstance(value, (list, tuple)):
        return [str(name) for name in value]
    return []


def read(path: Path) -> dict[str, Any]:
    if path.suffix.lower() == ".engine":
        for candidate in (path.with_suffix(".engine.meta.json"), path.with_suffix(".meta.json")):
            try:
                payload = json.loads(candidate.read_text(encoding="utf-8"))
                if isinstance(payload, dict):
                    return {"classes": _names(payload.get("classes") or payload.get("names") or [])}
            except (OSError, ValueError, TypeError):
                pass

    try:
        import torch

        checkpoint = torch.load(path, map_location="cpu", weights_only=False)
        if isinstance(checkpoint, dict):
            for container in (checkpoint.get("visionWorkbench"), checkpoint.get("meta")):
                if isinstance(container, dict) and (container.get("classes") or container.get("names")):
                    return {"classes": _names(container.get("classes") or container.get("names"))}
    except Exception:
        pass

    try:
        from ultralytics import YOLO  # type: ignore

        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            model = YOLO(str(path))
        return {"names": _names(getattr(model, "names", {}))}
    except Exception:
        return {"classes": []}


if __name__ == "__main__":
    try:
        print(json.dumps(read(Path(sys.argv[1]).resolve()), ensure_ascii=False))
    except (IndexError, OSError, ValueError) as error:
        print(json.dumps({"error": str(error), "classes": []}, ensure_ascii=False))
