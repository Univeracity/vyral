def package:
  if type == "array" then
    if length == 1 then .[0] else error("expected one npm package") end
  elif type == "object" then
    to_entries
    | if length == 1 then .[0].value else error("expected one npm package") end
  else
    error("unexpected npm pack JSON shape")
  end;

package
| .files as $files
| ($files | type == "array") and
  ([$files[].path] | index("LICENSE")) and
  ([$files[].path] | index("README.md")) and
  ([$files[].path] | index("src/index.js")) and
  ([$files[].path] | index("src/index.d.ts")) and
  all($files[].path; test("(^|/)(Inbox|docs|\\.env|\\.claude)(/|$)") | not)
