import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
import os


def data_dir():
    path = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local" / "share")) / "VisionAgent"
    path.mkdir(parents=True, exist_ok=True)
    return path


def audit_logger():
    logger = logging.getLogger("vision_agent.audit")
    logger.setLevel(logging.INFO)
    if not logger.handlers:
        handler = RotatingFileHandler(data_dir() / "events.jsonl", maxBytes=2_000_000, backupCount=3, encoding="utf-8")
        handler.setFormatter(logging.Formatter('%(message)s'))
        logger.addHandler(handler)
    return logger


def log_event(logger, event, **fields):
    from datetime import datetime, timezone
    logger.info(json.dumps({"time": datetime.now(timezone.utc).isoformat(), "event": event, **fields}, ensure_ascii=False))
