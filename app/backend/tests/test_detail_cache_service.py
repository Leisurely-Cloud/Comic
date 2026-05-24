from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

# Import directly to avoid importing the entire services package
import importlib.util
spec = importlib.util.spec_from_file_location(
    "detail_cache_service",
    str(Path(__file__).resolve().parents[1] / "services" / "detail_cache_service.py")
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
DetailCacheService = module.DetailCacheService


class TestDetailCacheService(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.cache_file = os.path.join(self.tmpdir, "cache.json")
        self.legacy_file = os.path.join(self.tmpdir, "legacy.json")
        self.warnings: list[str] = []
        self.service = DetailCacheService(
            cache_file=self.cache_file,
            legacy_cache_file=self.legacy_file,
            on_warning=lambda msg: self.warnings.append(msg),
        )

    def tearDown(self):
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_load_empty_when_no_files(self):
        result = self.service.load()
        self.assertEqual(result, {})

    def test_load_from_cache_file(self):
        data = {"key1": {"title": "漫画1"}}
        with open(self.cache_file, "w", encoding="utf-8") as f:
            json.dump(data, f)

        result = self.service.load()
        self.assertEqual(result, data)

    def test_load_from_legacy_file(self):
        data = {"key2": {"title": "漫画2"}}
        with open(self.legacy_file, "w", encoding="utf-8") as f:
            json.dump(data, f)

        result = self.service.load()
        self.assertEqual(result, data)

    def test_save_creates_file(self):
        data = {"key": {"title": "漫画"}}
        self.service.save(data)

        self.assertTrue(os.path.exists(self.cache_file))
        with open(self.cache_file, "r", encoding="utf-8") as f:
            loaded = json.load(f)
        self.assertEqual(loaded, data)

    def test_save_atomic_write(self):
        data = {"key": {"title": "漫画"}}
        self.service.save(data)

        tmp_file = self.cache_file + ".tmp"
        self.assertFalse(os.path.exists(tmp_file))
        self.assertTrue(os.path.exists(self.cache_file))

    def test_save_creates_directory(self):
        nested_dir = os.path.join(self.tmpdir, "nested", "dir")
        nested_file = os.path.join(nested_dir, "cache.json")
        service = DetailCacheService(cache_file=nested_file)

        service.save({"key": "value"})
        self.assertTrue(os.path.exists(nested_file))

    def test_load_invalid_json_returns_empty(self):
        with open(self.cache_file, "w", encoding="utf-8") as f:
            f.write("invalid json {{{")

        result = self.service.load()
        self.assertEqual(result, {})
        self.assertTrue(len(self.warnings) > 0)

    def test_load_non_dict_returns_empty(self):
        with open(self.cache_file, "w", encoding="utf-8") as f:
            json.dump([1, 2, 3], f)

        result = self.service.load()
        self.assertEqual(result, {})

    def test_make_cache_key_with_custom_key(self):
        class Adapter:
            def get_manga_cache_key(self, url):
                return f"custom:{url}"

        adapter = Adapter()
        key = self.service._make_cache_key(adapter, "https://example.com")
        self.assertEqual(key, "custom:https://example.com")

    def test_make_cache_key_default(self):
        class Adapter:
            pass

        adapter = Adapter()
        key = self.service._make_cache_key(adapter, "https://example.com")
        self.assertEqual(key, "https://example.com")

    def test_make_cache_key_strips_whitespace(self):
        class Adapter:
            pass

        adapter = Adapter()
        key = self.service._make_cache_key(adapter, "  https://example.com  ")
        self.assertEqual(key, "https://example.com")


if __name__ == "__main__":
    unittest.main()
