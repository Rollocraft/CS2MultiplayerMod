const fs = require("fs");
const path = require("path");

class CSSPresencePlugin {
  apply(compiler) {
    compiler.hooks.afterEmit.tap("CSSPresencePlugin", (compilation) => {
      const outputPath = compilation.outputOptions.path;
      if (!outputPath) return;
      if (!fs.existsSync(outputPath)) {
        fs.mkdirSync(outputPath, { recursive: true });
      }
      for (const chunk of compilation.chunks) {
        const cssFile = `${chunk.name}.css`;
        const fullPath = path.join(outputPath, cssFile);
        if (!fs.existsSync(fullPath)) {
          fs.writeFileSync(fullPath, "/* auto-generated empty css */");
        }
      }
    });
  }
}

module.exports = { CSSPresencePlugin };
