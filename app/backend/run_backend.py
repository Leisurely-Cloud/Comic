import sys
from pathlib import Path


APP_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(APP_DIR))

from backend.api.server import run_server


if __name__ == "__main__":
    run_server(port=18765)
