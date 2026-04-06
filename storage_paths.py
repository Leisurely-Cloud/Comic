import os
import sys


APP_STORAGE_ENV_VAR = "COMIC_DOWNLOAD_DIR"
APP_STORAGE_DIR_NAME = "ComicDownloads"
APP_STATE_DIR_NAME = ".comic_state"


def normalize_path(path: str) -> str:
    return os.path.abspath(os.path.expandvars(os.path.expanduser(path)))


def get_user_home_dir() -> str:
    home_dir = os.path.expanduser("~")
    if home_dir and home_dir != "~":
        return home_dir
    return os.environ.get("USERPROFILE") or os.getcwd()


def get_storage_root_dir() -> str:
    override_dir = (os.environ.get(APP_STORAGE_ENV_VAR) or "").strip()
    if override_dir:
        return normalize_path(override_dir)

    home_dir = get_user_home_dir()
    downloads_dir = os.path.join(home_dir, "Downloads")
    if os.path.isdir(downloads_dir):
        return os.path.join(downloads_dir, APP_STORAGE_DIR_NAME)
    return os.path.join(home_dir, APP_STORAGE_DIR_NAME)


def ensure_storage_root_dir() -> str:
    storage_root_dir = get_storage_root_dir()
    os.makedirs(storage_root_dir, exist_ok=True)
    return storage_root_dir


def get_runtime_state_dir() -> str:
    runtime_state_dir = os.path.join(ensure_storage_root_dir(), APP_STATE_DIR_NAME)
    os.makedirs(runtime_state_dir, exist_ok=True)
    return runtime_state_dir


def get_resume_state_file_path() -> str:
    return os.path.join(get_runtime_state_dir(), "download_resume_data.json")


def get_manga_detail_cache_file_path() -> str:
    return os.path.join(get_runtime_state_dir(), "manga_detail_cache.json")


def get_legacy_project_root_dir(current_file: str) -> str:
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(current_file))
