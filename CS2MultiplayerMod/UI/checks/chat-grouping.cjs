const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const ts = require("typescript");

const source = fs.readFileSync(path.join(__dirname, "../src/mods/chat-grouping.ts"), "utf8");
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } });
const groupingExports = {};
vm.runInNewContext(compiled.outputText, { exports: groupingExports });
const { groupChatMessages } = groupingExports;

const message = (id, sender, timestamp, isLocal = false) => ({
    id, sender, timestamp, isLocal, kind: "message", text: "message " + id, time: "19:12",
});
const groups = groupChatMessages([
    message(1, "Alex", 1000), message(2, "Alex", 2000),
    message(3, "Maya", 3000), message(4, "Alex", 4000),
    message(5, "Alex", 124001), message(6, "Alex", 124002, true),
]);
assert.equal(groups.length, 5, "a different speaker, long gap, or local identity starts a new header");
assert.equal(groups[0].entries.map(entry => entry.id).join(","), "1,2", "nearby consecutive messages share one header");
assert.equal(groupChatMessages([message(1, "Alex", 1000), message(2, "Alex", 121000)]).length, 1, "the two-minute boundary groups");
assert.equal(groupChatMessages([message(1, "Alex", 1000), message(2, "Alex", 121001)]).length, 2, "a gap longer than two minutes gets a header");
assert.equal(groupChatMessages([message(1, "Alex", 1000), message(2, "Alex", 900)]).length, 2, "out-of-order timestamps get a header");
assert.equal(groupChatMessages([message(1, "Alex", 1000), message(2, "Alex", 86401000)]).length, 2, "the same clock time the next day is a separate group");
assert.equal(groupChatMessages([
    { ...message(1, "Alex", undefined), time: "23:59" },
    { ...message(2, "Alex", undefined), time: "00:00" },
]).length, 1, "the older-assembly fallback works across midnight");
assert.equal(groupChatMessages([
    { ...message(1, "Alex", undefined), time: "19:12" },
    { ...message(2, "Alex", undefined), time: "19:15" },
]).length, 2, "the fallback still separates distant messages");
assert.equal(groupChatMessages([{ ...message(1, null, 1000), kind: "activity" }]).length, 0, "activity never renders as player chat");
console.log("Chat grouping checks passed.");
