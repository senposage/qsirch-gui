# Qsirch POST filter behavior

This note records the behavior that made Qsirch POST filtering appear random.
It is intentionally specific. Do not replace the observations below with a
friendlier interpretation in a future client without a regression test.

## Short version

`tools` does not mean one thing in Qsirch:

| Where it appears | Actual role | Does it filter results? |
| --- | --- | --- |
| `GET /search?tools=1` or `tools=2` | asks Qsirch to calculate search-tool facets | No |
| `GET /search?tools_resp=<mode>` | changes the facet response presentation | No |
| `GET /search/tools` | returns the available metadata-tool keys by category | No |
| POST body `tools` | raw Qsirch search expression appended to `q` | Yes, if it is valid expression syntax |
| POST body `tools_hits` | selected facet hints for the response | No |
| POST body `preferences` | per-request tool presentation preferences | No |

The parameter naming makes it very easy to send the wrong representation.

## What the server does

The Qsirch 7 server POST handler accepts `tools` from the JSON body as a
string. It stores that string temporarily, then constructs the effective query
by concatenating it to the normal query parameter:

```text
effective query = q + " " + tools
```

It parses that combined text with its normal query parser. It does not parse
`tools` as a JSON filter object.

Consequences:

- `{"tools": "Email"}` is not an Email category filter. It is an extra word
  in the search query, which can return mixed file types or no results.
- `{"tools": {"category": "Email"}}` is not supported by this endpoint.
  Depending on request serialization it either becomes invalid search text or
  fails request validation.
- A valid expression works through POST because it becomes part of the normal
  search string.

## Verified category syntax

The verified Qsirch expression form is:

```text
category:<CategoryName>
```

Examples observed on Qsirch 7:

```text
category:Email
category:Images
category:Documents
```

`category:Email` returned only `.eml` results in the tested result page.
`category:Images` and `category:Documents` also constrained the result set to
their respective categories.

The category names should come from the service, not a hard-coded client list.
`GET /qsirch/latest/api/search/tools?filter_syntax=true&lang=en` returns a
`categories` map and associated metadata keys. The category map included
`Documents`, `Email`, `Images`, `Music`, and `Videos` on the verified service.

## Requests that look similar but are not equivalent

### Ignored top-level category parameter

```http
GET /qsirch/latest/api/search?q=wind&category=Email
```

This did not constrain results on the verified server. The same holds for
generic top-level `ext` and `type` parameters.

### Correct expression in a normal GET search

```http
GET /qsirch/latest/api/search?q=wind%20category%3AImages&limit=25&store_history=0
```

This is a real search expression and constrained the category.

### Correct legacy POST bridge

```http
POST /qsirch/latest/api/search?q=.&limit=25&store_history=0
Content-Type: application/json

{
  "tools": "category:Email",
  "tools_resp": 0,
  "tools_hits": [],
  "preferences": {}
}
```

This returned only email results on the verified service. Its effect is the
same as appending `category:Email` to the ordinary query string.

## Native client behavior

The official browser-based UI does not use a POST JSON object to apply normal
facet selections. It stores selected values in its client-side structured `q`
route state and refreshes the GET search. It separately requests facets using:

```http
GET /qsirch/latest/api/search?q=<query>&tools=2&tools_resp=3&tools_limit_items=5
```

That response is a list of available values for facet controls such as modified
date and size. It is not the filtered result list.

The exact browser route serialization for every metadata facet has not yet been
promoted to a public SDK contract. Treat only the category expression examples
above as verified filter syntax.

## Upstream client guidance

1. Do not expose a generic `post_search(filters: dict)` API. It would promise a
   contract Qsirch does not provide.
2. Name POST support `search_post_expression` and require a non-empty string.
3. Keep `store_history=0` as the default for library searches and test probes.
4. Provide `list_search_tools` for discovery, but do not claim its returned
   metadata keys are all immediately usable expressions.
5. Add a live or recorded regression test for each metadata expression before
   documenting it as supported.
6. Prefer a normal GET `search` call for standard search expressions. POST is
   a legacy bridge, not a superior filtering API.

## No-write audit boundary

Search is not necessarily side-effect free by default: Qsirch can record a
search-history entry unless `store_history=0` is provided. The clean-room
client adds this parameter to its search methods. Thumbnail, preview,
metadata-extraction, download, file-action, and async-context routes remain
outside this protocol harness because they may trigger generation, caching, or
other server-side work.
