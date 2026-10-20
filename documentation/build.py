"""Build the public Markdown guides into a standalone static website."""
from __future__ import annotations

import html
import json
import posixpath
import re
import shutil
import tempfile
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urljoin, urlsplit, urlunsplit

import markdown
import yaml
from jinja2 import Environment, FileSystemLoader, select_autoescape
from markupsafe import Markup
from pygments.formatters import HtmlFormatter

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "help"
OUTPUT = ROOT / "site"
CONFIG = json.loads((ROOT / "documentation/site.json").read_text(encoding="utf-8"))
EXTENSIONS = [
    "abbr", "admonition", "attr_list", "def_list", "footnotes", "md_in_html",
    "sane_lists", "tables", "toc", "codehilite", "pymdownx.details",
    "pymdownx.highlight", "pymdownx.inlinehilite", "pymdownx.keys",
    "pymdownx.superfences", "pymdownx.tabbed",
]


def page_url(slug: str) -> str:
    return "" if slug == "index" else slug + "/"


def add_metadata(page: dict) -> dict:
    canonical = urljoin(CONFIG["url"], page["url"])
    title = page["meta"].get("seo_title") or f'{page["title"]} | {CONFIG["name"]}'
    page.update(canonical=canonical, seo_title=title)
    if page["slug"] == "404":
        return page
    website = {"@type": "WebSite", "@id": CONFIG["url"] + "#website",
               "url": CONFIG["url"], "name": CONFIG["name"], "inLanguage": "en"}
    webpage = {"@type": "WebPage", "@id": canonical + "#webpage", "url": canonical,
               "name": title, "description": page["description"], "inLanguage": "en",
               "isPartOf": {"@id": website["@id"]},
               "author": {"@type": "Person", "name": "Rollocraft", "url": "https://rollocraft.me/"}}
    graph = [website, webpage]
    if page["slug"] != "index":
        breadcrumb = {"@type": "BreadcrumbList", "@id": canonical + "#breadcrumb",
                      "itemListElement": [
                          {"@type": "ListItem", "position": 1, "name": "Documentation", "item": CONFIG["url"]},
                          {"@type": "ListItem", "position": 2, "name": page["title"], "item": canonical}]}
        webpage["breadcrumb"] = {"@id": breadcrumb["@id"]}
        graph.append(breadcrumb)
    page["structured_data"] = {"@context": "https://schema.org", "@graph": graph}
    return page


class ContentParser(HTMLParser):
    """Collect document links, IDs and searchable sections from rendered HTML."""
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.ids: set[str] = set()
        self.links: list[str] = []
        self.sections: list[dict] = []
        self.section = {"heading": "", "anchor": "", "text": ""}
        self.heading = False
        self.ignored = 0
        self.skip_permalink = False

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if "id" in attrs:
            self.ids.add(attrs["id"])
        for key in ("href", "src"):
            if attrs.get(key):
                self.links.append(attrs[key])
        if tag in ("script", "style"):
            self.ignored += 1
        if tag in ("h1", "h2", "h3", "h4"):
            if self.section["text"].strip() or self.section["heading"]:
                self.sections.append(self.section)
            self.section = {"heading": "", "anchor": attrs.get("id", ""), "text": ""}
            self.heading = True
        if tag == "a" and "headerlink" in attrs.get("class", ""):
            self.skip_permalink = True
        if tag in ("p", "li", "td", "th", "br", "pre"):
            self.section["text"] += " "

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self.ignored -= 1
        if tag in ("h1", "h2", "h3", "h4"):
            self.heading = False
        if tag == "a":
            self.skip_permalink = False
        if tag in ("p", "li", "td", "th", "pre"):
            self.section["text"] += " "

    def handle_data(self, data):
        if self.ignored or self.skip_permalink:
            return
        self.section["heading" if self.heading else "text"] += data

    def finish(self):
        self.sections.append(self.section)
        for section in self.sections:
            for key in ("heading", "text"):
                section[key] = " ".join(section[key].split())
        return self.sections


def rewrite_links(content: str, slug: str) -> str:
    prefix = "../" if slug != "index" else "./"

    def rewrite(match):
        attr, quote, value = match.groups()
        parts = urlsplit(html.unescape(value))
        if parts.scheme or parts.netloc or parts.path.startswith("/"):
            return match.group(0)
        path = parts.path
        if path.endswith(".md"):
            path = prefix + page_url(Path(path).stem)
        elif path.startswith("assets/"):
            path = prefix + path
        return attr + "=" + quote + html.escape(urlunsplit(parts._replace(path=path)), quote=True) + quote

    return re.sub(r'(href|src)=([\"\x27])(.*?)\2', rewrite, content)


def read_page(source: Path) -> dict:
    text = source.read_text(encoding="utf-8-sig")
    meta = {}
    if text.startswith("---\n"):
        _, front, text = text.split("---\n", 2)
        meta = yaml.safe_load(front) or {}
    md = markdown.Markdown(extensions=EXTENSIONS, extension_configs={
        "toc": {"permalink": True}, "codehilite": {"guess_lang": False},
        "pymdownx.tabbed": {"alternate_style": True},
    })
    content = rewrite_links(md.convert(text), source.stem)
    content = re.sub(r"(<table\b.*?</table>)", r'<div class="table-wrapper" role="region" tabindex="0" aria-label="Scrollable table">\1</div>', content, flags=re.S)
    parser = ContentParser()
    parser.feed(content)
    sections = parser.finish()
    title = meta.get("title") or next((s["heading"] for s in sections if s["heading"]), source.stem)
    description = meta.get("description") or next((s["text"] for s in sections if s["text"]), CONFIG["description"])[:170]
    toc = md.toc_tokens
    if len(toc) == 1 and toc[0]["level"] == 1:
        toc = toc[0]["children"]
    return add_metadata({"slug": source.stem, "title": title, "description": description,
            "content": Markup(content), "toc": toc, "sections": sections,
            "meta": meta, "url": page_url(source.stem)})


def validate_site(destination: Path) -> None:
    documents = {}
    for file in destination.rglob("*.html"):
        parser = ContentParser()
        parser.feed(file.read_text(encoding="utf-8"))
        documents[file.relative_to(destination).as_posix()] = parser
    errors = []
    for name, doc in documents.items():
        for link in doc.links:
            url = urlsplit(link)
            if url.scheme or url.netloc:
                continue
            target = unquote(url.path)
            path = posixpath.normpath(posixpath.join(posixpath.dirname(name), target)) if target else name
            if target.startswith("/"):
                path = target.lstrip("/")
            if (destination / path).is_dir():
                path = path.rstrip("/") + "/index.html"
            path = path.removeprefix("./")
            if not (destination / path).is_file():
                errors.append(f"{name}: missing target {link}")
            elif url.fragment and path in documents and unquote(url.fragment) not in documents[path].ids:
                errors.append(f"{name}: missing anchor {link}")
    for stylesheet in destination.rglob("*.css"):
        for asset in re.findall(r"url\(['\"]?([^)'\"]+)", stylesheet.read_text(encoding="utf-8")):
            if not urlsplit(asset).scheme and not (stylesheet.parent / asset).is_file():
                errors.append(f"{stylesheet.name}: missing asset {asset}")
    if errors:
        raise ValueError("Broken internal links:\n" + "\n".join(sorted(set(errors))))
    print(f"Validated {len(documents)} HTML pages, local assets and anchor links.")


def build() -> None:
    pages = {p.stem: read_page(p) for p in sorted(SOURCE.glob("*.md"))}
    pages["index"].update(group="Documentation", label="Home")
    ordered = [pages["index"]]
    for group in CONFIG["navigation"]:
        for slug, label in group["pages"]:
            page = pages[slug]
            page.update(group=group["title"], label=label)
            ordered.append(page)
    if set(pages) != {p["slug"] for p in ordered}:
        raise ValueError("Every guide except the home page must be listed in documentation/site.json")
    env = Environment(loader=FileSystemLoader(ROOT / "documentation/templates"),
                      autoescape=select_autoescape(["html"]))
    env.globals["page_url"] = page_url
    template = env.get_template("page.html")
    # Validate in a temporary directory before replacing any existing build.
    with tempfile.TemporaryDirectory(prefix="cs2mp-docs-") as temporary:
        stage = Path(temporary)
        shutil.copytree(SOURCE / "assets", stage / "assets")
        syntax = HtmlFormatter(style="friendly").get_style_defs(".highlight")
        syntax += "\n" + HtmlFormatter(style="monokai").get_style_defs('[data-theme="dark"] .highlight')
        (stage / "assets/syntax.css").write_text(syntax, encoding="utf-8")
        index = []
        for i, page in enumerate(ordered):
            prefix = "../" if page["slug"] != "index" else "./"
            rendered = template.render(config=CONFIG, page=page, prefix=prefix,
                                       previous=ordered[i - 1] if i else None,
                                       following=ordered[i + 1] if i + 1 < len(ordered) else None)
            target = stage / page["url"] / "index.html"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(rendered, encoding="utf-8")
            for section in page["sections"]:
                index.append({"title": page["title"], "heading": section["heading"],
                              "text": section["text"], "group": page["group"],
                              "url": page["url"] + ("#" + section["anchor"] if section["anchor"] else "")})
        # A script index also works when the output is opened from a static host without APIs.
        (stage / "assets/search-index.js").write_text("window.DOCS_INDEX=" + json.dumps(index, ensure_ascii=False).replace("<", "\\u003c") + ";\n", encoding="utf-8")
        missing = add_metadata({"slug": "404", "title": "Page not found", "group": "Documentation", "url": "404.html",
                   "description": "This guide may have moved. Find your way back to the documentation.",
                   "toc": [], "meta": {}, "content": Markup('<h1>Page not found</h1><p>This page could not be found. Search the documentation or return to the home page.</p><p><a class="btn btn--primary" href="' + CONFIG["url"] + '">Back to the documentation <span aria-hidden="true">↗</span></a></p>')})
        (stage / "404.html").write_text(template.render(config=CONFIG, page=missing, prefix=CONFIG["url"], previous=None, following=None), encoding="utf-8")
        (stage / ".nojekyll").touch()
        (stage / "CNAME").write_text(urlsplit(CONFIG["url"]).netloc + "\n", encoding="utf-8")
        (stage / "robots.txt").write_text("User-agent: *\nAllow: /\nSitemap: " + CONFIG["url"] + "sitemap.xml\n", encoding="utf-8")
        sitemap = '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">'
        sitemap += "".join("<url><loc>" + html.escape(urljoin(CONFIG["url"], p["url"])) + "</loc></url>" for p in ordered)
        (stage / "sitemap.xml").write_text(sitemap + "</urlset>\n", encoding="utf-8")
        validate_site(stage)
        if OUTPUT.is_symlink() or OUTPUT.resolve() != ROOT / "site":
            raise ValueError("Output must be the repository's site directory")
        if OUTPUT.exists():
            shutil.rmtree(OUTPUT)
        shutil.copytree(stage, OUTPUT)
    print(f"Built {len(pages)} guides and {len(index)} searchable sections in {OUTPUT}")


if __name__ == "__main__":
    build()
