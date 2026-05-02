from __future__ import annotations

from typing import Any, Callable, Optional, Tuple

from backend.models import MangaDetailRequest, MangaDetailResult
from backend.support.site_adapters import get_adapter, resolve_adapter_from_url


class MangaDetailService:
    """获取漫画详情的服务层。"""

    def fetch_detail(
        self,
        request: MangaDetailRequest,
        *,
        cache_detail: Optional[Callable[[Any, str, Any], None]] = None,
        fallback_detail_getter: Optional[Callable[[Any, str], Tuple[Any, str]]] = None,
    ) -> MangaDetailResult:
        url = (request.url or "").strip()
        fallback_site_key = request.fallback_site_key or ""

        adapter = resolve_adapter_from_url(url, fallback_key=fallback_site_key)
        if adapter is None:
            adapter = get_adapter(fallback_site_key)

        try:
            detail = adapter.fetch_manga_detail(url)
            if cache_detail is not None:
                cache_detail(adapter, url, detail)
            return MangaDetailResult(adapter=adapter, detail=detail)
        except Exception:
            if fallback_detail_getter is not None:
                detail, source = fallback_detail_getter(adapter, url)
                if detail is not None:
                    return MangaDetailResult(
                        adapter=adapter,
                        detail=detail,
                        used_fallback=True,
                        fallback_source=source,
                    )
            raise
