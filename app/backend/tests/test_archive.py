from __future__ import annotations

import os
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from backend.support.archive import (
    build_cbz_comicinfo_xml,
    build_cbz_export_dir,
    build_unique_archive_path,
    create_zip_archive_for_manga,
    list_exportable_image_files,
)


class TestBuildUniqueArchivePath(unittest.TestCase):
    def test_first_path(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            manga_dir = os.path.join(tmpdir, "manga")
            os.makedirs(manga_dir)
            result = build_unique_archive_path(manga_dir)
            self.assertEqual(result, os.path.join(tmpdir, "manga.zip"))

    def test_second_path_when_first_exists(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            manga_dir = os.path.join(tmpdir, "manga")
            os.makedirs(manga_dir)
            with open(os.path.join(tmpdir, "manga.zip"), "w") as f:
                f.write("test")
            result = build_unique_archive_path(manga_dir)
            self.assertEqual(result, os.path.join(tmpdir, "manga_2.zip"))


class TestCreateZipArchiveForManga(unittest.TestCase):
    def test_creates_zip(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            manga_dir = os.path.join(tmpdir, "manga")
            os.makedirs(os.path.join(manga_dir, "001_第1话"))
            with open(os.path.join(manga_dir, "001_第1话", "page1.jpg"), "w") as f:
                f.write("test")

            archive_path, count = create_zip_archive_for_manga(manga_dir)
            self.assertTrue(os.path.exists(archive_path))
            self.assertEqual(count, 1)

    def test_skips_temp_dirs(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            manga_dir = os.path.join(tmpdir, "manga")
            os.makedirs(os.path.join(manga_dir, "001_第1话"))
            os.makedirs(os.path.join(manga_dir, ".下载中_002_第2话"))
            with open(os.path.join(manga_dir, "001_第1话", "page1.jpg"), "w") as f:
                f.write("test")
            with open(os.path.join(manga_dir, ".下载中_002_第2话", "page1.jpg"), "w") as f:
                f.write("test")

            archive_path, count = create_zip_archive_for_manga(manga_dir)
            self.assertEqual(count, 1)

    def test_raises_on_nonexistent_dir(self):
        with self.assertRaises(FileNotFoundError):
            create_zip_archive_for_manga("/nonexistent/path")


class TestBuildCbzExportDir(unittest.TestCase):
    def test_creates_export_dir(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            manga_dir = os.path.join(tmpdir, "manga")
            result = build_cbz_export_dir(manga_dir)
            self.assertTrue(os.path.isdir(result))
            self.assertTrue(result.endswith("_CBZ"))


class TestListExportableImageFiles(unittest.TestCase):
    def test_lists_images(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            with open(os.path.join(tmpdir, "page1.jpg"), "w") as f:
                f.write("test")
            with open(os.path.join(tmpdir, "page2.png"), "w") as f:
                f.write("test")
            with open(os.path.join(tmpdir, "page3.txt"), "w") as f:
                f.write("test")

            result = list_exportable_image_files(tmpdir)
            self.assertEqual(len(result), 2)

    def test_empty_directory(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            result = list_exportable_image_files(tmpdir)
            self.assertEqual(len(result), 0)

    def test_nonexistent_directory(self):
        result = list_exportable_image_files("/nonexistent/path")
        self.assertEqual(len(result), 0)


class TestBuildCbzComicinfoXml(unittest.TestCase):
    def test_builds_xml(self):
        xml_bytes = build_cbz_comicinfo_xml(
            manga_title="Test Manga",
            chapter_title="Chapter 1",
            chapter_number=1,
            chapter_count=10,
            page_count=20,
            manga_url="https://example.com",
        )
        self.assertIn(b"<ComicInfo>", xml_bytes)
        self.assertIn(b"<Series>Test Manga</Series>", xml_bytes)
        self.assertIn(b"<Title>Chapter 1</Title>", xml_bytes)
        self.assertIn(b"<Number>1</Number>", xml_bytes)

    def test_handles_empty_values(self):
        xml_bytes = build_cbz_comicinfo_xml(
            manga_title="",
            chapter_title="",
            chapter_number=None,
            chapter_count=None,
            page_count=0,
        )
        self.assertIn(b"<ComicInfo>", xml_bytes)


if __name__ == "__main__":
    unittest.main()
