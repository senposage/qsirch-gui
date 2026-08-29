# Qsirch protocol prototype

This directory is a clean-room, protocol-focused reference for the Qsirch 7
REST service. It is intentionally independent of the PyQsirchgui WPF project.
Nothing here imports WPF code, references its models, or assumes its settings
format.

For the full investigation of the overloaded `tools` parameter and the
verified POST expression form, see [POST_FILTER_BEHAVIOR.md](POST_FILTER_BEHAVIOR.md).
For the upstream caveat-by-caveat status, see
[UPSTREAM_QUIRK_AUDIT.md](UPSTREAM_QUIRK_AUDIT.md).

## Scope

The prototype contains only read-oriented, verified service behavior:

- session login and one automatic retry after a 401 response
- paged index search using `q`, `limit`, `offset`, `advanced_mode`, `sort_by`,
  and `sort_dir`, with `store_history=0` by default
- the legacy POST expression bridge, explicitly named to distinguish it from
  structured filters
- search-tool discovery with `/search/tools`
- optional server content highlighting with `highlight=content`
- indexed directory lookup with `/search-dirs`
- extension/category discovery with `/list-extensions`
- more-like-this lookup using a result identifier
- Qsirch's cached-result `async-search` context and result-slice route
- thumbnail retrieval through the dynamic result action URL, with the `size`
  and `flags` values used by QNAP's desktop client
- result-specific preview target selection and optional binary retrieval after
  a user explicitly requests a preview

The prototype does not call the metadata-extraction route. Search responses
already include their available metadata, whereas the separate route can start
or poll extraction work.

It deliberately excludes file mutations, downloads, settings, and history.
Preview retrieval can make Qsirch generate or cache derived content, and async
search creates a short-lived server-side cache context; both are included only
because they were explicitly exercised in this parity pass.

## Verified behavior

The findings below were observed against Qsirch 7 and are suitable for a
small, self-contained upstream contribution after tests are added there.

- `/search-dirs` returns directory matches separately from ordinary search
  results. The returned paths are slash-delimited NAS paths, with only `name`,
  `display`, and numeric `type` data rather than ordinary result metadata.
  On the verified service it honors plain or quoted terms such as `wind`, but
  not fielded expressions such as `name:"wind"`; it rejects limits above 100.
  It is therefore a narrow, optional folder supplement rather than a
  replacement for normal paged search.
- `/list-extensions` returns the indexed extension/category catalogue.
- Generic top-level GET filters such as `ext`, `type`, and `category` are
  ignored by the service. The native client instead carries selected values in
  its structured `q` route state.
- POST `tools` is **not** a JSON filter object despite its name. The server
  parser accepts it as a string, appends it to `q`, then parses both values as
  a single Qsirch search expression. Posting `{"tools": "Email"}` therefore
  means "also search for the word Email" and produces mixed, misleading
  results. This was the source of the observed POST filter issue.
- The verified category expression is `category:<CategoryName>`. For example,
  `category:Email` returns indexed email files and `category:Images` returns
  indexed image files. The `search_post_expression` method sends this form
  correctly. It includes `store_history=0` and only uses the search route.
- `/search/tools?filter_syntax=true&lang=en` returns the supported metadata
  keys grouped by category. On the verified Qsirch 7 instance, Email exposes
  `bcc`, `cc`, `from`, `sent_date`, and `to`; this endpoint describes keys, it
  does not itself filter results.
- Do not infer that every key returned by `/search/tools` has a universally
  working expression syntax. Category syntax is verified. Metadata expressions
  need an isolated regression case before being published as supported.
- `sort_by` and `sort_dir` are the accepted sorting parameters. `title` is not
  a usable `sort_by` value; `name`, `modified`, `created`, and `size` are.
- Both `modified` and `created` have consistently ordered results for `asc`
  and `desc` on the verified service. Prefer that server ordering whenever a
  result list is paged; a client can apply a secondary presentation sort after
  it has received the relevant page. `title` returns an empty result set.
- `highlight=content` can return matching content fragments marked with
  `<qusion>` tags. The field is optional and not every result provides one.
- Search results contain dynamic URLs under `actions`. Standard thumbnails are
  requested from `actions.thumbnail`; QNAP's desktop client adds `flags=2` and
  `size=500` when they are absent.
- Image and rich-media previews use `preview.image_default` rather than the
  standard result thumbnail. The official client requests `size=800&flags=2`
  for a focused photo, video-cover, or audio-cover display. A smaller
  `size=400` is used for media posters and fallback images.
- The result's `preview.container_type` selects the native rendering branch:
  `media_viewer` for images, `document` for PDF-style rendering, and
  `online_viewer` for Office/online-viewer handling. The official client sends
  PDFs through its bundled PDF viewer using the item `actions.open` URL.
  Video and audio playback also use `actions.open`; video asks the browser to
  preload metadata and uses `preview.image_default` as its poster.
- Qsirch's result `actions.preview` endpoint returns a small JSON rendering
  descriptor. On the verified server, Email descriptors include
  `container_type`, `html`, `image_default`, `number_of_page`, and
  `preview_page`; PDF and DOCX descriptors include `container_type`,
  `image_default`, `number_of_page`, `preview_page`, and `source_data`.
  The descriptor is not itself the rendered document.
- For the verified PDF and DOCX fixtures, `actions.open` returned an
  `application/pdf` stream. The QNAP client can therefore use the same PDF
  renderer for native PDFs and converted Office documents. This behavior is
  server-dependent: the target must be requested only after the user chooses
  to preview the item.
- A focused image `preview.image_default` request returned a PNG at
  `size=800&flags=2`. On the same server, selected video and audio descriptors
  exposed poster/thumbnail URLs that returned HTTP 404. Treat that as "no
  generated media preview" and retain the normal file action instead of
  treating it as a failed search.
- `/async-search` is a cached-result contract, not a server-push stream. Its
  response contains `context.cid`, `context.url`, and `context.size`, plus the
  original result `total`. Fetching the context URL with `from` and `size`
  returns an `items` slice; that response's `total` is the slice length, not
  the original global total.
- `advanced_mode=1` and `advanced_mode=2` are accepted request parameters,
  but OCR-only search is intentionally deferred. It depends on NAS-side OCR
  indexing and optional components, so a portable client must not promise a
  result until that capability has been enabled and indexed on the NAS.
- Existing metadata is returned in `metadata.all`, with `key`, `value`,
  `display`, and `level` fields. `preview.info` carries a presentation-ready
  subset, including the full path and any indexed author/title/producer or
  media-quality values. Verified examples included author/title/producer for
  Office/PDF documents, image quality for pictures, and duration/channels/
  sample rate for MP3 files.
- The author field is embedded metadata, not an OCR inference. The bundled
  extractor maps Apache Tika values such as `Author`, `meta:author`, and
  `dc:creator`, plus ExifTool values such as `Author`, `By-line`, and
  `Creators`, into Qsirch metadata. OCR is a separate image-text pipeline.
  A future client can therefore show or locally filter an Author column from
  the normal search response without an additional metadata request. Treat it
  as untrusted descriptive metadata: it may be an Office profile, template,
  scanner/exporter, camera, or email value rather than the actual creator.
  Qsirch reports filesystem ownership separately in the top-level `owner`
  field. Do not use either field for visibility or access decisions.
- The full NAS path belongs in `preview.info` under its `path` key. The top
  level `path` may only be the parent directory.
- `.` or a single space matches broadly. `*` is not a wildcard query.

## Upstream PR boundary

If this is contributed to `iios-co/qsirch`, keep the change limited to its
Python REST client and API documentation. Do not include the WPF application,
Windows path mapping, UI behavior, or deployment files.

## Safe local smoke checks

Set `QSIRCH_HOST`, `QSIRCH_USER`, and `QSIRCH_PASS`, then use the client from a
short script to call `search`, `search_post_expression`, `search_directories`,
`list_extensions`, `similar`, `async_search`, or `list_search_tools`. Search methods send
`store_history=0` so they do not create Qsirch search-history entries.
Avoid calling `thumbnail` or `fetch_preview` during a no-write audit because
the NAS may generate or cache derived preview content while serving it.
