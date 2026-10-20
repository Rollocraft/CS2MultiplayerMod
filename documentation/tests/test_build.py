"""Migration checks: public routes, in-game help targets, and static assets."""
import importlib.util
import json
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

SPEC = importlib.util.spec_from_file_location("docs_build", Path(__file__).resolve().parents[1] / "build.py")
build = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build)


class MetadataParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.meta = {}
        self.canonical = None
        self.title = ""
        self.language = None
        self.json_ld = ""
        self.in_title = False
        self.in_json_ld = False

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "html":
            self.language = attrs.get("lang")
        if tag == "title":
            self.in_title = True
        if tag == "meta":
            self.meta[attrs.get("name") or attrs.get("property")] = attrs.get("content")
        if tag == "link" and attrs.get("rel") == "canonical":
            self.canonical = attrs["href"]
        if tag == "script" and attrs.get("type") == "application/ld+json":
            self.in_json_ld = True

    def handle_endtag(self, tag):
        if tag == "title":
            self.in_title = False
        if tag == "script":
            self.in_json_ld = False

    def handle_data(self, text):
        if self.in_title:
            self.title += text
        if self.in_json_ld:
            self.json_ld += text


class DocumentationTests(unittest.TestCase):
    def test_all_guides_have_unique_metadata_and_consistent_social_previews(self):
        titles, descriptions = set(), set()
        for source in build.SOURCE.glob("*.md"):
            page = build.read_page(source)
            parser = MetadataParser()
            parser.feed((build.OUTPUT / page["url"] / "index.html").read_text(encoding="utf-8"))
            with self.subTest(page=source.stem):
                self.assertEqual(parser.language, "en")
                self.assertTrue(parser.title)
                self.assertNotIn(parser.title, titles)
                self.assertNotIn(parser.meta["description"], descriptions)
                self.assertEqual(parser.title, page["seo_title"])
                self.assertEqual(parser.canonical, build.CONFIG["url"] + page["url"])
                self.assertEqual(parser.meta["og:url"], parser.canonical)
                for prefix in ("og", "twitter"):
                    self.assertEqual(parser.meta[prefix + ":title"], parser.title)
                    self.assertEqual(parser.meta[prefix + ":description"], parser.meta["description"])
                    self.assertTrue(parser.meta[prefix + ":image"].startswith(build.CONFIG["url"]))
                    self.assertTrue(parser.meta[prefix + ":image:alt"])
                graph = json.loads(parser.json_ld)["@graph"]
                webpage = next(node for node in graph if node["@type"] == "WebPage")
                self.assertEqual(webpage["url"], parser.canonical)
                self.assertEqual(webpage["description"], parser.meta["description"])
                if source.stem != "index":
                    trail = next(node for node in graph if node["@type"] == "BreadcrumbList")["itemListElement"]
                    self.assertEqual(trail[0]["item"], build.CONFIG["url"])
                    self.assertEqual(trail[-1]["item"], parser.canonical)
            titles.add(parser.title)
            descriptions.add(parser.meta["description"])

    def test_sitemap_covers_all_guides_and_excludes_the_404_page(self):
        xml = ET.parse(build.OUTPUT / "sitemap.xml")
        urls = [node.text for node in xml.findall(".//{http://www.sitemaps.org/schemas/sitemap/0.9}loc")]
        expected = {build.CONFIG["url"] + build.page_url(p.stem) for p in build.SOURCE.glob("*.md")}
        self.assertEqual(set(urls), expected)
        self.assertEqual(len(urls), len(expected))
        self.assertIn("Sitemap: " + build.CONFIG["url"] + "sitemap.xml", (build.OUTPUT / "robots.txt").read_text())
        parser = MetadataParser()
        parser.feed((build.OUTPUT / "404.html").read_text(encoding="utf-8"))
        self.assertIn("noindex", parser.meta["robots"])
        self.assertFalse(parser.json_ld)

    def test_markdown_links_keep_queries_anchors_and_subpath_hosting(self):
        content = '<a href="mods.md?status=works#compatibility-list">Mods</a><img src="assets/img/ui-join-relay.webp"><a href="https://example.org/readme.md">External</a>'
        result = build.rewrite_links(content, "getting-started")
        self.assertIn('href="../mods/?status=works#compatibility-list"', result)
        self.assertIn('src="../assets/img/ui-join-relay.webp"', result)
        self.assertIn('href="https://example.org/readme.md"', result)

    def test_home_and_local_fragment_links(self):
        content = '<a href="index.md">Home</a><a href="#install">Install</a>'
        self.assertIn('href="../"', build.rewrite_links(content, "getting-started"))
        self.assertIn('href="#install"', build.rewrite_links(content, "getting-started"))
        self.assertIn('href="./"', build.rewrite_links(content, "index"))

    def test_all_in_game_help_links_still_resolve(self):
        source = (build.ROOT / "CS2MultiplayerMod/Game/HelpLinks.cs").read_text(encoding="utf-8")
        links = re.findall(r'"([\w-]+\.md(?:#[\w-]+)?)"', source)
        self.assertGreater(len(links), 15)
        for link in links:
            url = urlsplit(link)
            slug = Path(url.path).stem
            target = build.OUTPUT / build.page_url(slug) / "index.html"
            with self.subTest(link=link):
                self.assertTrue(target.is_file())
                parser = build.ContentParser()
                parser.feed(target.read_text(encoding="utf-8"))
                if url.fragment:
                    self.assertIn(url.fragment, parser.ids)

    def test_every_public_guide_is_rendered_and_indexed(self):
        script = (build.OUTPUT / "assets/search-index.js").read_text(encoding="utf-8")
        index = json.loads(script.removeprefix("window.DOCS_INDEX=").removesuffix(";\n"))
        indexed_paths = {urlsplit(entry["url"]).path for entry in index}
        for source in build.SOURCE.glob("*.md"):
            self.assertIn(build.page_url(source.stem), indexed_paths)
        for entry in index:
            url = urlsplit(entry["url"])
            target = build.OUTPUT / url.path / "index.html"
            parser = build.ContentParser()
            parser.feed(target.read_text(encoding="utf-8"))
            if url.fragment:
                self.assertIn(unquote(url.fragment), parser.ids, entry["url"])
            self.assertNotIn("¶", entry["text"])

    def test_mod_table_keeps_script_and_static_scroll_container(self):
        content = (build.OUTPUT / "mods/index.html").read_text(encoding="utf-8")
        self.assertIn('src="../assets/mod-table.js"', content)
        self.assertIn('class="table-wrapper"', content)
        self.assertIn("Road Speed Adjuster", content)
        self.assertIn("Tested by", content)
        self.assertNotIn("mkdocs", content.lower())

    def test_validator_rejects_broken_fragments_and_assets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "index.html").write_text('<h1 id="present">Hi</h1><a href="#missing">Broken</a>', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "missing anchor"):
                build.validate_site(root)
            (root / "index.html").write_text('<a href="#present">OK</a><h1 id="present">Hi</h1>', encoding="utf-8")
            (root / "style.css").write_text("@font-face { src: url('missing.woff2'); }", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "missing asset"):
                build.validate_site(root)

    def test_search_sections_ignore_scripts_and_permalink_glyphs(self):
        parser = build.ContentParser()
        parser.feed('<h2 id="sync">Smart sync<a class="headerlink" href="#sync">¶</a></h2><p>Check <strong>treasury</strong>.</p><script>not_searchable()</script>')
        section = parser.finish()[0]
        self.assertEqual("Smart sync", section["heading"])
        self.assertEqual("Check treasury.", section["text"])


if __name__ == "__main__":
    unittest.main()
