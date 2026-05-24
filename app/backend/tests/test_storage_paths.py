from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from backend.support.storage_paths import (
    APP_STORAGE_DIR_NAME,
    APP_STORAGE_ENV_VAR,
    APP_STATE_DIR_NAME,
    ensure_storage_root_dir,
    get_storage_root_dir,
    normalize_path,
)


class TestNormalizePath(unittest.TestCase):
    def test_normalizes_separators(self):
        path = "C:\\Users\\test\\downloads"
        result = normalize_path(path)
        self.assertEqual(result, os.path.normpath(path))

    def test_expands_user(self):
        path = "~/downloads"
        result = normalize_path(path)
        self.assertIn(os.path.expanduser("~"), result)


class TestGetStorageRootDir(unittest.TestCase):
    def test_with_env_var(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {APP_STORAGE_ENV_VAR: tmpdir}):
                result = get_storage_root_dir()
                self.assertEqual(result, os.path.normpath(tmpdir))

    def test_without_env_var(self):
        env = os.environ.copy()
        env.pop(APP_STORAGE_ENV_VAR, None)
        with patch.dict(os.environ, env, clear=True):
            result = get_storage_root_dir()
            # The function uses ~/Downloads/ComicDownloads, not ~/ComicDownloads
            expected = os.path.join(os.path.expanduser("~"), "Downloads", APP_STORAGE_DIR_NAME)
            self.assertEqual(result, os.path.normpath(expected))


class TestEnsureStorageRootDir(unittest.TestCase):
    def test_creates_directory(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            target = os.path.join(tmpdir, "new_dir")
            with patch.dict(os.environ, {APP_STORAGE_ENV_VAR: target}):
                result = ensure_storage_root_dir()
                self.assertTrue(os.path.isdir(result))
                self.assertEqual(result, os.path.normpath(target))

    def test_existing_directory(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {APP_STORAGE_ENV_VAR: tmpdir}):
                result = ensure_storage_root_dir()
                self.assertEqual(result, os.path.normpath(tmpdir))


if __name__ == "__main__":
    unittest.main()
