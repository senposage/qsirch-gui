# Upstream Qsirch quirk audit

This is an audit of the caveats published in the upstream `iios-co/qsirch`
client as compared with live Qsirch 7 observations. It is a protocol reference,
not a commitment to copy upstream behavior into the WPF application.

## Status key

- **Verified**: exercised against the Qsirch 7 server using a read-oriented
  request with `store_history=0` where applicable.
- **Corrected**: upstream wording or behavior is inaccurate on the tested
  service.
- **Guarded**: the clean-room client handles the condition without a separate
  live probe.
- **Partially verified**: a bounded subset of the behavior has a live fixture;
  callers must not generalize beyond that subset.
- **Deferred**: intentionally outside the no-write audit boundary.
- **Open**: needs a specific regression case before it becomes a supported
  public contract.

## Connection and session behavior

| Upstream note | Status | Finding |
| --- | --- | --- |
| QTS CGI login returns an `authSid` used as `NAS_SID` | Verified | Login succeeded and normal search/status calls accepted the resulting session. |
| A 401 can require re-authentication | Guarded | The reference client retains credentials and retries once after any 401. The exact Qsirch error payload is not used as a compatibility dependency. |
| HTTP default is port 8080 | Verified | The tested NAS has plain HTTP on 8080. |
| HTTPS is selected only by a scheme flag | Corrected | HTTPS must also use the NAS TLS port. On the tested NAS, HTTPS on 8080 fails with `WRONG_VERSION_NUMBER`; HTTPS on 443 works. The certificate is not trusted by default, so strict verification fails until a trusted certificate/CA is installed or verification is explicitly disabled for a trusted self-signed NAS. |

## Search and filters

| Upstream note | Status | Finding |
| --- | --- | --- |
| Top-level GET filters `ext`, `type`, `category`, and `q.*` are ignored | Verified | These parameters did not constrain the Qsirch 7 result set. |
| POST `tools=Email` is the only reliable category filter | Corrected | POST `tools` is a raw search-expression suffix, not a category value. `tools=Email` adds the word `Email` to the query. `tools=category:Email` works, as do verified `category:Images` and `category:Documents` expressions. |
| File type filtering should be client-side | Verified | This remains the portable default. The WPF client locally filters extensions; its Email case now uses the verified server expression as a narrow first pass. |
| `/search/tools` reveals category-specific metadata fields | Verified | It returns category names and metadata keys, including Email fields such as `from`, `to`, `cc`, `bcc`, and `sent_date`. It is a descriptor endpoint, not a filter endpoint. |
| Advanced query syntax supports phrases, boolean operations, exclusion, and grouping | Verified | Quoted phrases, `OR`, `AND`, `NOT`, unary `-`, and parentheses all returned distinct result sets. `AND` binds more tightly than `OR`; parentheses should be used whenever a client needs to make mixed boolean intent explicit. |
| `.` or a single space searches broadly; `*` does not | Verified | `.` and a space matched broadly; `*` returned no results. |
| Image OCR modes `1` and `2` are available | Deferred | The server accepts both values, but OCR-only mode returned no matches on the sampled NAS before OCR indexing was enabled. It depends on NAS-side indexing and optional components, so no client contract is published yet. |
| `/search-dirs` behaves like ordinary fielded search | Corrected | With `wind`, it returned 64 directory paths in about 300 ms. Plain and quoted terms worked; `name:"wind"` and `name:wind` returned none. Its records contain only slash-delimited paths plus a numeric type, and a limit of 500 returns HTTP 400 while 100 succeeds. Treat it as an optional, capped folder supplement, not a replacement for paged result search. |

## Pagination and sorting

| Upstream note | Status | Finding |
| --- | --- | --- |
| `sort_by` and `sort_dir` are the accepted names | Verified | `modified` and `created` order correctly in both directions. |
| A missing sort direction defaults to ascending | Verified | `sort_by=modified` returned the same ordering as explicit `sort_dir=asc`. |
| Relevance ignores direction | Verified | Changing `sort_dir` with `sort_by=relevance` did not change the sampled order. |
| `title` sort is broken | Verified | `sort_by=title` returned an empty set on the tested server. |
| A practical page maximum is about 500 | Partially verified | Keep 500 as the conservative page cap used by upstream and the current clients. The observed behavior above that was not sufficient to establish a server-enforced maximum, so do not present 500 as a hard protocol limit. |

## Result payload and presentation

| Upstream note | Status | Finding |
| --- | --- | --- |
| The full file path is in `preview.info[path]`, not always top-level `path` | Verified | Use the preview metadata path first. |
| Search results contain metadata fields | Verified | `metadata.all` provides indexed values; `owner` is a separate top-level field. |
| Author values are useful descriptive data | Corrected | Author values are extractor metadata from sources such as Office, PDF, image, audio, and mail metadata. They are not a reliable creator, Windows user, access-control identity, or OCR-derived authority. |
| Search actions are dynamic URLs | Verified | Standard thumbnails and open/download/preview actions are returned per item. Requests that may generate content remain outside the no-write harness. |
| Thumbnails use the action URL | Verified | QNAP's desktop client adds `flags=2` and `size=500` when needed. The reference client records this but does not fetch thumbnails during no-write smoke checks. |
| Rich image/media previews use `preview.image_default` | Verified | The official UI uses it with `size=800&flags=2` for focused previews. |
| Preview action returns a renderer-ready document or image | Corrected | `actions.preview` is a JSON descriptor, not the final rendered artifact. Email descriptors contained HTML-related fields; PDF/DOCX descriptors contained page/source fields. On the verified server, `actions.open` for tested PDF and DOCX items returned `application/pdf`, including Office conversion. |
| Media poster/thumbnail URLs always exist when a media descriptor exists | Corrected | A full-size image preview returned a PNG. Sampled video and audio descriptors supplied poster/thumbnail routes that returned HTTP 404, so clients must present a no-preview state rather than treat the result as broken. |

## Deferred and open endpoints

| Area | Status | Reason |
| --- | --- | --- |
| Email HTML preview action | Verified | The preview action returned a JSON descriptor with `html`, `image_default`, `number_of_page`, and `preview_page`. The email body was intentionally not inspected or retained. |
| Downloads | Deferred | Transfers file content and creates local output. |
| Metadata extraction route | Deferred | May start or poll extractor work. Normal search metadata is sufficient for the current client. |
| More-like-this endpoint | Verified | `/more-like-this/<item-id>` returned a bounded, ordinary result payload. Ranking semantics and privacy expectations still need product-level review before a UI promotes it. |
| Async search contexts | Verified | `/async-search` synchronously creates a cached context containing `cid`, `url`, and `size`; `/async-search-resp/<cid>?from=&size=` returns result slices. It is not a push/streaming API, and the slice `total` is not the original global total. |
| Metadata-field expression grammar | Partially verified | `from:\"value\"` with `category:Email`, `modified:\"Previous 30 days\"`, `modified:Yesterday`, and `size:10KB..1MB` returned constrained result sets. Add one fixture per additional field before advertising it. |

## Local application notes

The WPF app is kept separate from this prototype, but two direct defects were
corrected after this audit:

- enabling HTTPS with the default 8080 HTTP port now changes the default port
  to 443, without overwriting a custom port;
- the Email filter no longer POSTs `tools=Email`; it uses a GET search with the
  verified `category:Email` expression and preserves extension filtering.

## Upstream PR order

1. Replace the upstream POST category behavior with explicit search-expression
   support and document `category:<name>`.
2. Add `store_history=0` to library search defaults.
3. Clamp requested page size to 500 before the request is sent.
4. Add `/search/tools` discovery with careful wording that the response is
   descriptive, not proof of executable expression grammar.
5. Add a transport note: HTTP 8080 and HTTPS 443 are common defaults, but the
   client must treat port, TLS, and certificate validation as separate settings.
6. Add guarded preview-target helpers: render documents from `actions.open`,
   focused images from `preview.image_default`, and make video/audio poster
   404 a normal no-preview state.
7. Add async context helpers with explicit wording that the service caches a
   completed query rather than streaming incremental results.
8. Keep OCR search deferred until a tested NAS advertises and actually serves
   OCR-indexed content.
