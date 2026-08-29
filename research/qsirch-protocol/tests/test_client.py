import sys
import unittest
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from client import PreviewTarget, QsirchProtocolClient


class QsirchProtocolClientTests(unittest.TestCase):
    def test_full_path_uses_preview_metadata(self):
        item = {
            "path": "/share/parent",
            "preview": {"info": [{"key": "path", "value": "/share/parent/file.pdf"}]},
        }

        self.assertEqual("/share/parent/file.pdf", QsirchProtocolClient.full_path(item))

    def test_metadata_returns_indexed_values_without_an_api_call(self):
        item = {
            "metadata": {
                "all": [
                    {"key": "author", "value": "Example Author"},
                    {"key": "pages", "value": 12},
                ]
            }
        }

        self.assertEqual(
            {"author": "Example Author", "pages": 12},
            QsirchProtocolClient.metadata(item),
        )

    def test_image_preview_url_sets_high_resolution_defaults(self):
        item = {"preview": {"image_default": "/preview/image?obj_id=abc"}}

        url = QsirchProtocolClient.image_preview_url(item)

        self.assertTrue(url.startswith("/preview/image?"))
        self.assertIn("obj_id=abc", url)
        self.assertIn("flags=2", url)
        self.assertIn("size=800", url)

    def test_image_preview_url_preserves_existing_settings(self):
        item = {"preview": {"image_default": "/preview/image?obj_id=abc&size=400&flags=1"}}

        url = QsirchProtocolClient.image_preview_url(item, size=800, flags=2)

        self.assertIn("flags=2", url)
        self.assertIn("size=800", url)

    def test_category_expression_uses_verified_qsirch_syntax(self):
        self.assertEqual("category:Email", QsirchProtocolClient.category_expression("Email"))

    def test_category_expression_rejects_display_text_with_spaces(self):
        with self.assertRaises(ValueError):
            QsirchProtocolClient.category_expression("Email messages")

    def test_post_expression_sends_a_string_not_a_json_filter(self):
        client = QsirchProtocolClient("example.test")
        response = Mock()
        response.json.return_value = {"items": [], "total": 0}
        response.raise_for_status.return_value = None
        client._request = Mock(return_value=response)

        client.search_post_expression("wind", "category:Email", limit=25, offset=5)

        client._request.assert_called_once_with(
            "POST",
            "/qsirch/latest/api/search",
            params={"q": "wind", "limit": 25, "offset": 5, "store_history": 0},
            json={
                "tools": "category:Email",
                "tools_resp": 0,
                "tools_hits": [],
                "preferences": {},
            },
        )

    def test_post_expression_requires_a_non_empty_string(self):
        client = QsirchProtocolClient("example.test")

        with self.assertRaises(ValueError):
            client.search_post_expression("wind", "  ")

    def test_async_search_uses_the_cached_result_endpoint(self):
        client = QsirchProtocolClient("example.test")
        response = Mock()
        response.json.return_value = {"context": {"cid": "abc"}, "total": 3}
        response.raise_for_status.return_value = None
        client._request = Mock(return_value=response)

        client.async_search("wind", limit=25, offset=5, sort_by="modified", sort_dir="asc")

        client._request.assert_called_once_with(
            "GET",
            "/qsirch/latest/api/async-search",
            params={
                "q": "wind",
                "limit": 25,
                "offset": 5,
                "advanced_mode": 0,
                "store_history": 0,
                "sort_by": "modified",
                "sort_dir": "asc",
            },
        )

    def test_async_results_uses_context_url_and_slice_parameters(self):
        client = QsirchProtocolClient("example.test")
        response = Mock()
        response.json.return_value = {"items": []}
        response.raise_for_status.return_value = None
        client._request = Mock(return_value=response)

        client.async_results({"url": "/qsirch/latest/api/async-search-resp/abc"}, start=4, size=25)

        client._request.assert_called_once_with(
            "GET",
            "/qsirch/latest/api/async-search-resp/abc",
            params={"from": 4, "size": 25},
        )

    def test_media_preview_target_uses_high_resolution_image_url(self):
        item = {
            "preview": {
                "container_type": "media_viewer",
                "image_default": "/preview/image?obj_id=abc",
            }
        }

        target = QsirchProtocolClient("example.test").preview_target(item)

        self.assertEqual("image", target.kind)
        self.assertIn("size=800", target.url)

    def test_document_preview_target_uses_the_open_action(self):
        item = {
            "preview": {"container_type": "document"},
            "actions": {"open": "/open?obj_id=abc"},
        }

        target = QsirchProtocolClient("example.test").preview_target(item)

        self.assertEqual(PreviewTarget("document", "/open?obj_id=abc", "document"), target)

    def test_preview_target_rejects_missing_rendering_data(self):
        with self.assertRaises(ValueError):
            QsirchProtocolClient("example.test").preview_target({})

    def test_similar_uses_a_bounded_result_request(self):
        client = QsirchProtocolClient("example.test")
        response = Mock()
        response.json.return_value = {"items": []}
        response.raise_for_status.return_value = None
        client._request = Mock(return_value=response)

        client.similar("item-123", limit=600)

        client._request.assert_called_once_with(
            "GET",
            "/qsirch/latest/api/more-like-this/item-123",
            params={"limit": 500, "store_history": 0},
        )

    def test_directory_search_uses_its_smaller_verified_limit(self):
        client = QsirchProtocolClient("example.test")
        response = Mock()
        response.json.return_value = {"items": []}
        response.raise_for_status.return_value = None
        client._request = Mock(return_value=response)

        client.search_directories("wind", limit=500)

        client._request.assert_called_once_with(
            "GET",
            "/qsirch/latest/api/search-dirs",
            params={"q": "wind", "limit": 100},
        )

    def test_fetch_preview_returns_content_and_content_type(self):
        client = QsirchProtocolClient("example.test")
        response = Mock()
        response.content = b"preview-bytes"
        response.url = "https://example.test/preview"
        response.headers = {"Content-Type": "image/png; charset=binary"}
        response.raise_for_status.return_value = None
        client._request = Mock(return_value=response)

        payload = client.fetch_preview(PreviewTarget("image", "/preview", "media_viewer"))

        self.assertEqual(b"preview-bytes", payload.content)
        self.assertEqual("image/png", payload.content_type)
        self.assertEqual("https://example.test/preview", payload.url)


if __name__ == "__main__":
    unittest.main()
