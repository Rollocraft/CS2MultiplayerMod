# Documentation website

A custom static website. Markdown is rendered with Python-Markdown and Jinja;
the published site contains only HTML, CSS, JavaScript, fonts and images.
No application server, MkDocs, JavaScript framework or external search service is needed.

From the repository root:

```sh
python -m pip install -r docs-requirements.txt
python documentation/build.py
python -m http.server 8000 --directory site
```

Open http://localhost:8000. On Windows the existing environment can be used as
`.venv/Scripts/python.exe` in place of `python`.

Edit player guides in `help/`. Register new guides in `documentation/site.json`;
the home page is built separately and reached through the header logo.
Front matter supports `title`, `seo_title`, `description` and `extra_javascript`. Markdown
links, images, heading anchors, admonitions, tables and fenced code are supported.
Existing public URLs and anchors remain stable. `docs/` contains private developer
notes and is never an input to this build.

Each page has its own search title and description, a canonical URL, Open Graph
and Twitter metadata, and WebPage/WebSite structured data. Guide pages also have
BreadcrumbList markup. The sitemap includes every guide; the 404 page is marked
noindex. Metadata and sitemap consistency are checked by the Python tests.

Layout lives in `documentation/templates/page.html`; styling and browser behavior
live in `help/assets/`. The mod compatibility table retains its own filters and
shareable query parameters. Search indexes every guide by section, ranks title
matches first and works entirely in the browser, with Ctrl/Cmd+K, arrow keys and
Escape. Article text and navigation remain available without JavaScript. The GitHub star count loads from the public repository API and is cached for an hour per browser tab; the GitHub link remains usable if that request fails.

The build checks all local page, asset and anchor links before replacing `site/`.
It also emits a 404 page, search index, sitemap, robots.txt, CNAME and .nojekyll.
The Pages workflow builds and deploys `site/` on changes to documentation sources.
Build output must not be edited manually.

Verification:

```sh
python -m unittest discover -s documentation/tests
node --test documentation/tests/search.test.cjs documentation/tests/theme.test.cjs documentation/tests/navigation.test.cjs
```
