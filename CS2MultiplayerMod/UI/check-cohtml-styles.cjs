const fs = require("fs");
const path = require("path");
const ts = require("typescript");

// These declarations compile in browsers, but Cohtml 2.2 rejects them at runtime.
// Check the actual UI source before emitting a bundle, rather than relying on tsc.
const src = path.resolve(__dirname, "src");
const shorthands = new Set([
    "background", "border", "borderRadius", "borderColor", "borderStyle", "borderWidth",
    "borderTop", "borderRight", "borderBottom", "borderLeft", "margin", "padding",
]);
const errors = [];

function literal(node) {
    if (ts.isStringLiteralLike(node)) return node.text;
    if (ts.isTemplateExpression(node))
        return node.head.text + node.templateSpans.map(span => "${...}" + span.literal.text).join("");
    return "";
}

function checkFile(file) {
    const source = ts.createSourceFile(file, fs.readFileSync(file, "utf8"), ts.ScriptTarget.Latest, true);
    const constants = new Map();
    function collect(node) {
        if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer)
            constants.set(node.name.text, literal(node.initializer));
        ts.forEachChild(node, collect);
    }
    collect(source);
    function report(node, message) {
        const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
        errors.push(`${path.relative(src, file)}:${line + 1}: ${message}`);
    }
    function visit(node) {
        const value = literal(node);
        if (/var\([^)]*,/.test(value)) report(node, "Cohtml does not support var() fallback values.");
        if (ts.isPropertyAssignment(node)) {
            const name = node.name.getText(source).replace(/["']/g, "");
            const css = literal(node.initializer) || (ts.isIdentifier(node.initializer) ? constants.get(node.initializer.text) : "");
            if (shorthands.has(name) && css && css.includes("var("))
                report(node, `Cohtml requires longhand properties for CSS variables in ${name}.`);
        }
        ts.forEachChild(node, visit);
    }
    visit(source);
}

function checkStylesheet(file) {
    const lines = fs.readFileSync(file, "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
        const report = message => errors.push(`${path.relative(src, file)}:${index + 1}: ${message}`);
        if (/:not\s*\(/.test(line))
            report("Cohtml does not support :not() selectors; use an explicit enabled class.");
        if (/var\([^)]*,/.test(line))
            report("Cohtml does not support var() fallback values.");
    });
}

function walk(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const file = path.join(directory, entry.name);
        if (entry.isDirectory()) walk(file);
        else if (/\.tsx?$/.test(entry.name)) checkFile(file);
        else if (/\.(?:css|scss)$/.test(entry.name)) checkStylesheet(file);
    }
}

walk(src);
if (errors.length) {
    console.error(errors.join("\n"));
    process.exitCode = 1;
} else {
    console.log("Cohtml style compatibility checks passed.");
}
