"""Clean-room, read-oriented Qsirch 7 REST protocol reference.

This module is deliberately independent of the PyQsirchgui WPF application.
It is intended as a small, reviewable basis for upstream API-client changes.
"""

from __future__ import annotations

import base64
from dataclasses import dataclass
from typing import Any, Optional
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit
import xml.etree.ElementTree as element_tree

import requests


@dataclass(frozen=True)
class PreviewTarget:
    """A result-specific target that a caller can render with its own UI."""

    kind: str
    url: str
    container_type: str


@dataclass(frozen=True)
class BinaryPayload:
    """Bytes returned by a preview endpoint together with their content type."""

    content: bytes
    content_type: str
    url: str


class QsirchProtocolClient:
    """A minimal Qsirch client for verified read-oriented protocol routes."""

    def __init__(self, host: str, port: int = 8080, use_ssl: bool = False) -> None:
        scheme = "https" if use_ssl else "http"
        self.base_url = f"{scheme}://{host}:{port}"
        self.session = requests.Session()
        self._username: Optional[str] = None
        self._password: Optional[str] = None

    def login(self, username: str, password: str) -> bool:
        """Authenticate and retain credentials for one 401 retry."""
        self._username = username
        self._password = password
        return self._authenticate()

    def search(
        self,
        query: str,
        limit: int = 50,
        offset: int = 0,
        sort_by: Optional[str] = None,
        sort_dir: str = "desc",
        advanced_mode: int = 0,
        highlight: Optional[str] = None,
        highlight_limit: Optional[int] = None,
    ) -> dict[str, Any]:
        """Search the Qsirch index using the verified GET request shape."""
        params: dict[str, str | int] = {
            "q": query,
            "limit": max(1, min(limit, 500)),
            "offset": max(0, offset),
            "advanced_mode": advanced_mode,
            # Prevent an audit or client-side lookup from changing Qsirch history.
            "store_history": 0,
        }
        if sort_by and sort_by.lower() != "relevance":
            params["sort_by"] = sort_by
            params["sort_dir"] = sort_dir
        if highlight:
            params["highlight"] = highlight
        if highlight_limit is not None:
            params["highlight_limit"] = max(1, highlight_limit)

        response = self._request("GET", "/qsirch/latest/api/search", params=params)
        response.raise_for_status()
        return response.json()

    def search_post_expression(
        self,
        query: str,
        expression: str,
        limit: int = 50,
        offset: int = 0,
    ) -> dict[str, Any]:
        """Search through Qsirch's legacy POST expression bridge.

        Qsirch does *not* treat the JSON ``tools`` member as a structured
        filter. It appends this string to ``q`` and parses the combined value
        as Qsirch search syntax. For example, ``category:Email`` is a valid
        expression; a JSON value such as ``{\"category\": \"Email\"}`` is not.
        """
        if not isinstance(expression, str) or not expression.strip():
            raise ValueError("expression must be a non-empty Qsirch search expression")

        response = self._request(
            "POST",
            "/qsirch/latest/api/search",
            params={
                "q": query,
                "limit": max(1, min(limit, 500)),
                "offset": max(0, offset),
                "store_history": 0,
            },
            json={
                "tools": expression.strip(),
                "tools_resp": 0,
                "tools_hits": [],
                "preferences": {},
            },
        )
        response.raise_for_status()
        return response.json()

    def list_search_tools(self, filter_syntax: bool = True, lang: str = "en") -> dict[str, Any]:
        """Return the server's available metadata-tool keys by category.

        This describes available filters. It does not apply a filter to a
        search result and should not be confused with POST ``tools``.
        """
        response = self._request(
            "GET",
            "/qsirch/latest/api/search/tools",
            params={"filter_syntax": str(filter_syntax).lower(), "lang": lang},
        )
        response.raise_for_status()
        return response.json()

    @staticmethod
    def category_expression(category: str) -> str:
        """Build the verified expression form for a Qsirch result category."""
        if not isinstance(category, str) or not category.strip() or any(
            char.isspace() for char in category
        ):
            raise ValueError("category must be a non-empty Qsirch category key")
        return f"category:{category.strip()}"

    def search_directories(self, query: str, limit: int = 50) -> dict[str, Any]:
        """Return directory-only matches from Qsirch's native route.

        The tested Qsirch 7 service rejects directory requests above 100 with
        HTTP 400, unlike the ordinary search route which accepts up to 500.
        """
        response = self._request(
            "GET",
            "/qsirch/latest/api/search-dirs",
            params={"q": query, "limit": max(1, min(limit, 100))},
        )
        response.raise_for_status()
        return response.json()

    def list_extensions(self) -> dict[str, Any]:
        """Return the server's indexed extension/category catalogue."""
        response = self._request("GET", "/qsirch/latest/api/list-extensions")
        response.raise_for_status()
        return response.json()

    def similar(self, item_id: str, limit: int = 20) -> dict[str, Any]:
        """Return Qsirch's read-only more-like-this result payload for one item."""
        if not isinstance(item_id, str) or not item_id.strip():
            raise ValueError("item_id must be a non-empty Qsirch result identifier")

        response = self._request(
            "GET",
            f"/qsirch/latest/api/more-like-this/{item_id}",
            params={"limit": max(1, min(limit, 500)), "store_history": 0},
        )
        response.raise_for_status()
        return response.json()

    def async_search(
        self,
        query: str,
        limit: int = 50,
        offset: int = 0,
        sort_by: Optional[str] = None,
        sort_dir: str = "desc",
        advanced_mode: int = 0,
    ) -> dict[str, Any]:
        """Run Qsirch's cached-result search contract.

        Despite the endpoint name, the Qsirch 7 implementation completes the
        index query before it returns a context. The context lets a caller
        retrieve result slices afterwards; it is not a server-push stream.
        """
        params: dict[str, str | int] = {
            "q": query,
            "limit": max(1, min(limit, 500)),
            "offset": max(0, offset),
            "advanced_mode": advanced_mode,
            "store_history": 0,
        }
        if sort_by and sort_by.lower() != "relevance":
            params["sort_by"] = sort_by
            params["sort_dir"] = sort_dir

        response = self._request("GET", "/qsirch/latest/api/async-search", params=params)
        response.raise_for_status()
        return response.json()

    def async_results(
        self,
        context: dict[str, Any] | str,
        start: int = 0,
        size: int = 50,
    ) -> dict[str, Any]:
        """Fetch one slice from an ``async_search`` context.

        Qsirch returns the context URL in ``context[\"url\"]``. Its response
        ``total`` is the number of returned slice items, not the total reported
        by the original async-search response.
        """
        if isinstance(context, dict):
            path_or_url = context.get("url")
        else:
            path_or_url = (
                f"/qsirch/latest/api/async-search-resp/{context.strip()}"
                if isinstance(context, str) and context.strip()
                else None
            )
        if not isinstance(path_or_url, str) or not path_or_url.strip():
            raise ValueError("context must contain a non-empty async result URL or identifier")

        response = self._request(
            "GET",
            path_or_url,
            params={"from": max(0, start), "size": max(1, min(size, 500))},
        )
        response.raise_for_status()
        return response.json()

    def thumbnail(self, item: dict[str, Any], size: int = 500, flags: int = 2) -> bytes:
        """Fetch a result thumbnail using the dynamic action URL.

        The QNAP desktop client supplies ``flags=2`` and ``size=500`` when the
        returned action URL does not already specify those values.
        """
        action_url = item.get("actions", {}).get("thumbnail")
        if not action_url:
            raise ValueError("The search result does not provide a thumbnail action.")

        parts = urlsplit(action_url)
        params = dict(parse_qsl(parts.query, keep_blank_values=True))
        params.setdefault("flags", str(flags))
        params.setdefault("size", str(max(16, min(size, 2048))))
        action_url = urlunsplit(
            (parts.scheme, parts.netloc, parts.path, urlencode(params), parts.fragment)
        )
        response = self._request("GET", action_url, timeout=60)
        response.raise_for_status()
        if not response.headers.get("Content-Type", "").lower().startswith("image/"):
            raise ValueError("The thumbnail action did not return an image.")
        return response.content

    def preview_target(self, item: dict[str, Any], size: int = 800, flags: int = 2) -> PreviewTarget:
        """Choose the result URL appropriate to Qsirch's advertised preview type.

        The QNAP client renders these targets with bundled PDF and Office
        viewers. This protocol layer only chooses the target; applications
        remain responsible for rendering it.
        """
        preview = item.get("preview", {})
        actions = item.get("actions", {})
        container_type = str(preview.get("container_type", ""))

        if container_type == "media_viewer" and preview.get("image_default"):
            return PreviewTarget(
                kind="image",
                url=self.image_preview_url(item, size=size, flags=flags),
                container_type=container_type,
            )
        if container_type == "document" and actions.get("open"):
            return PreviewTarget(kind="document", url=str(actions["open"]), container_type=container_type)
        if container_type == "online_viewer" and actions.get("open"):
            return PreviewTarget(kind="online_document", url=str(actions["open"]), container_type=container_type)
        if actions.get("preview"):
            return PreviewTarget(kind="action", url=str(actions["preview"]), container_type=container_type)

        raise ValueError("The search result does not provide a supported preview target.")

    def fetch_preview(self, target: PreviewTarget, timeout: int = 60) -> BinaryPayload:
        """Fetch one preview target for an application-owned renderer.

        Preview access can make Qsirch generate or cache derived content.
        Call this only when the user has requested a preview.
        """
        response = self._request("GET", target.url, timeout=timeout)
        response.raise_for_status()
        return BinaryPayload(
            content=response.content,
            content_type=response.headers.get("Content-Type", "").split(";", 1)[0],
            url=response.url,
        )

    @staticmethod
    def image_preview_url(item: dict[str, Any], size: int = 800, flags: int = 2) -> str:
        """Build the high-resolution image-preview URL used by QNAP's client.

        This is intended for photo, video-cover, and audio-cover presentation.
        Callers decide whether retrieving the image is appropriate for their
        environment because the NAS may generate or cache it on first access.
        """
        image_default = item.get("preview", {}).get("image_default")
        if not image_default:
            raise ValueError("The search result does not provide an image preview URL.")

        parts = urlsplit(image_default)
        params = dict(parse_qsl(parts.query, keep_blank_values=True))
        params["flags"] = str(flags)
        params["size"] = str(max(16, min(size, 2048)))
        return urlunsplit(
            (parts.scheme, parts.netloc, parts.path, urlencode(params), parts.fragment)
        )

    @staticmethod
    def full_path(item: dict[str, Any]) -> str:
        """Get the full NAS path from a result's preview metadata."""
        for entry in item.get("preview", {}).get("info", []):
            if entry.get("key") == "path":
                return str(entry.get("value", ""))
        return str(item.get("path", ""))

    @staticmethod
    def metadata(item: dict[str, Any]) -> dict[str, Any]:
        """Return already-indexed metadata without invoking an extractor route."""
        metadata: dict[str, Any] = {}
        for entry in item.get("metadata", {}).get("all", []):
            key = entry.get("key")
            if key:
                metadata[str(key)] = entry.get("value")
        return metadata

    def _authenticate(self) -> bool:
        if not self._username or self._password is None:
            return False

        payload = {
            "user": self._username,
            "pwd": base64.b64encode(self._password.encode("utf-8")).decode("ascii"),
        }
        response = self.session.post(
            f"{self.base_url}/cgi-bin/authLogin.cgi", data=payload, timeout=15
        )
        response.raise_for_status()
        root = element_tree.fromstring(response.text)
        if root.findtext("authPassed") != "1":
            return False

        session_id = root.findtext("authSid")
        if not session_id:
            return False
        self.session.cookies.set("NAS_SID", session_id)
        return True

    def _request(self, method: str, path_or_url: str, **kwargs: Any) -> requests.Response:
        """Make an authenticated request and retry once after a 401 response."""
        url = (
            path_or_url
            if path_or_url.lower().startswith(("http://", "https://"))
            else f"{self.base_url}{path_or_url if path_or_url.startswith('/') else '/' + path_or_url}"
        )
        timeout = kwargs.pop("timeout", 20)
        response = self.session.request(method, url, timeout=timeout, **kwargs)
        if response.status_code != 401 or not self._authenticate():
            return response
        return self.session.request(method, url, timeout=timeout, **kwargs)
