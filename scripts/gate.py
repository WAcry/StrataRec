#!/usr/bin/env python3
"""The mechanical gate: the rules of AGENTS.md that a script can check, checked.

    python3 scripts/gate.py [--base <commit>]     every static check, then notes on the change: its size, the pages it made longer (under a second)
    python3 scripts/gate.py slow <TestResults>    the slowest tests of the last run (scripts/check.sh writes the reports)
    python3 scripts/gate.py selftest              proves that every check still bites, on a small made-up repository

A check that fails says what to do instead. Fix the code or the document; a budget or a baseline is raised only in a
commit of its own that says why (AGENTS.md section 6). After changing this file, run `selftest`.

The checks read text, not the compiled program, and each says what it covers: `names` finds a backticked name that no
longer occurs outside the documents (not whether it is declared, or means what the sentence says); `terms` reads the
names of public types and delegates (not members, configuration keys or comments); `commits` reads the commits of one
line of history (not merges); `comments` measures comment blocks and knows public API by the declaration under a block
and the types around it by indentation (not whether a comment repeats the code, or is worth its lines). What they
cannot see is for the review (.agents/skills/north-star-review).
"""
import glob
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

# The limits are wide on purpose: this is a large project, and each limit is there to stop what has clearly gone wrong (a
# page nobody reads to the end, a test waiting out a timeout), not to hold a page or a test at its present size.
AGENTS_LINES, SKILL_LINES, PAGE_LINES = 120, 120, {"docs/architecture.md": 160, "docs/glossary.md": 160, "docs/development.md": 300}
DECISION_CHARS = 80_000        # one area's decisions must fit in what an agent reads before it changes that area
SLEEP_BASELINE = 56            # Thread.Sleep / Task.Delay(<a time>) in tests: only goes down
SLOW_NOTE_S, SLOW_FAIL_S = 2.0, float(os.environ.get("GATE_SLOW_FAIL_S", 30))   # CI's shared runners set a wider limit
SIZE_PROJECTS, SIZE_LINES = 8, 1500   # nineteen in twenty changes here touch fewer src projects and add fewer src lines
# A comment is a document next to the code, read with every read of its file (docs/development.md section 11). Internal
# code and tests get a line or two at most; a public contract (what callers outside the assembly may rely on, which is
# often all a user's agent sees) rarely needs more than a screen of a type's members.
NOTE_LINES, CONTRACT_LINES = 4, 16
NOTE_SHARE = 0.2               # a change's comments in internal code, per line of code it adds, above which the gate notes it

# The judges (AGENTS.md section 6): directories end with a slash, files are whole names. The two build files turn
# warnings into errors and ban APIs for every project, which is a rule like the others.
RULES = ("AGENTS.md", ".agents/", ".claude/", "scripts/", ".github/", "BannedSymbols.txt", ".editorconfig",
         "Directory.Build.props", "Directory.Build.targets")
CODE = ("src/", "tests/", "samples/")
# The pages somebody pays to read: AGENTS.md and a skill's description in every session, README.md on a first visit, the
# rest when a task needs them. Not docs/decisions/: a decision is what the code cannot say.
PAGES = ("README.md", "AGENTS.md", "docs/", ".agents/skills/")
# Assemblies under src/ that see another's internals. New pairs are refused: make the member public with a contract, or
# move the code to the assembly that owns the knowledge.
FRIENDS = {("Core", "Features"), ("Core", "Pipeline"), ("Core", "Hosting"), ("Features", "Pipeline"),
           ("Pipeline", "Experiments"), ("Pipeline", "Hosting")}
# Names the documents may put in backticks although nothing in the repository declares them: they belong to .NET.
EXTERNAL = {"PoolingAsyncValueTaskMethodBuilder", "ServiceProvider", "EmbeddingBag", "JsonDocument", "ArrayPool",
            "DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION"}

TEXT = (".cs", ".json", ".jsonc", ".jsonl", ".csproj", ".props", ".targets", ".yml", ".sh", ".py", ".txt", ".sln")
WORD = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
POINTER = (re.compile(r"^(?:I?[A-Z][a-z0-9]+){2,}$"),        # CandidateTable, IServiceClient
           re.compile(r"^[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+$"),    # COLUMN_NO_PRODUCER
           re.compile(r"^[a-z][a-z0-9]*(?:_[a-z0-9]+)+$"))    # timeout_ms
PATH = re.compile(r"^(?:src|tests|samples|docs|scripts|\.github|\.agents)/[\w./@-]+$")
EXCUSE = re.compile(r"(?://|/\*|^\s*\*).*\b(TODO|FIXME|HACK)\b")   # in a comment; a string or a name may say them
SLEEP = re.compile(r"Thread\.Sleep\s*\(|Task\.Delay\s*\((?!\s*Timeout\.Infinite)")
# Words that only a comment narrating the past uses. Few on purpose: "no longer", "previously", "the old version" also
# describe what a program does at run time, and a check that is wrong teaches to game it. The rest is for the review.
HISTORY = re.compile(r"\bused to be\b|\b(?:it|they|we|this|that|which|who|one|what) used to\b|\b(?:formerly|originally)\b"
                     r"|\b(?:at|in|since|before|after|until|as of|commit) (?=[0-9a-f]*[a-f])(?=[0-9a-f]*\d)[0-9a-f]{7,12}\b"
                     r"|\b20\d\d-\d\d-\d\d(?=\s*[:)])|\b(?:was|were|has been|have been) fixed\b|\breview (?:round|found)\b", re.I)
DECLARED = re.compile(r"^(\s*)((?:(?:public|internal|private|protected|file|sealed|static|abstract|readonly|partial|ref|unsafe|new)\s+)*)"
                      r"(class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)")
RAW_QUOTES = re.compile(r'"{3,}')
TAG_ONLY = re.compile(r"^/{2,}\s*(?:</?(?:summary|remarks|para|returns|value|example|code|list|item|description|term)\b[^>]*>\s*)*$")
PUBLIC_TYPE = re.compile(r"^\s*public\s+(?:(?:sealed|static|abstract|readonly|partial|ref|unsafe)\s+)*"
                         r"(?:(?:class|struct|interface|enum|record(?:\s+(?:struct|class))?)\s+([A-Za-z_][A-Za-z0-9_]*)"
                         r"|delegate\s+[^;(]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*[<(])", re.M)


class Repo:
    def __init__(self, root):
        self.root = os.path.abspath(root)
        self.failures = []
        self.notes = []
        listed = self.git("ls-files", "-co", "--exclude-standard", "-z")   # -z: a path with other than ASCII is not quoted
        if listed is None:  # not a git repository (the self-test's): every file
            listed = "\0".join(os.path.relpath(os.path.join(d, f), self.root).replace(os.sep, "/")
                               for d, _, fs in os.walk(self.root) for f in fs)
        self.paths = sorted(p for p in listed.split("\0") if p and os.path.isfile(os.path.join(self.root, p)))

    def git(self, *args):
        done = subprocess.run(("git", "-C", self.root) + args, capture_output=True, text=True)
        return done.stdout if done.returncode == 0 else None

    def read(self, path):
        try:
            with open(os.path.join(self.root, path), encoding="utf-8") as f:
                return f.read()
        except (OSError, UnicodeDecodeError):
            return ""

    def under(self, prefixes, suffixes=None):
        return [p for p in self.paths if p.startswith(prefixes) and (suffixes is None or p.endswith(suffixes))]

    def fail(self, check, where, message):
        self.failures.append(f"gate: {check}: {where}: {message}")


def budgets(repo):
    """The pages every agent reads stay short, and one area's decisions stay loadable."""
    limits = dict(PAGE_LINES, **{"AGENTS.md": AGENTS_LINES})
    limits.update({p: SKILL_LINES for p in repo.under((".agents/skills/",), ("SKILL.md",))})
    for path, limit in sorted(limits.items()):
        lines = len(repo.read(path).splitlines())
        if lines > limit:
            repo.fail("budget", path, f"{lines} lines, the budget is {limit}. Cut what the code, a test or a check already "
                      "says, or move detail that is needed only sometimes into a skill or docs/; do not raise the budget.")
    for path in repo.under(("docs/decisions/",), (".md",)):
        chars = len(repo.read(path))
        if chars > DECISION_CHARS:
            repo.fail("budget", path, f"{chars} characters, the budget is {DECISION_CHARS}. Delete entries that only retell "
                      "the code, or split the area in two (.agents/skills/record-decision).")


def opens(modifiers):
    words = modifiers.split()
    return ("public" in words or "protected" in words) and "private" not in words


def comment_blocks(text, public_partials, contracts=True):
    """The comment blocks of a C# file: (first line, last line, lines of text, documents public API). A line of only XML
    tags (<summary>) or nothing is not text. A block documents public API when the declaration under it is public or
    protected and every type around it is too; types are told apart by indentation. Lines inside a raw string literal
    (a JSONC configuration in a test) are not comments."""
    lines = text.split("\n")
    blocks, types, raw, i = [], [], False, 0

    def around(indent):
        for depth, public, kind in reversed(types):
            if depth < indent:
                return public, kind
        return True, None

    def public_type(match):
        modifiers, name = match.group(2), match.group(4)
        explicit = re.search(r"\b(?:public|internal|private|protected|file)\b", modifiers)
        return opens(modifiers) or (not explicit and "partial" in modifiers.split() and name in public_partials)

    while i < len(lines):
        stripped = lines[i].strip()
        if raw or not stripped.startswith("//"):
            declared = None if raw else DECLARED.match(lines[i])
            if declared:
                depth = len(declared.group(1))
                while types and types[-1][0] >= depth:
                    types.pop()
                types.append((depth, around(depth)[0] and public_type(declared), declared.group(3)))
            if len(RAW_QUOTES.findall(lines[i])) % 2 == 1:
                raw = not raw
            i += 1
            continue
        start = i
        while i < len(lines) and lines[i].strip().startswith("//"):
            i += 1
        public = False
        if contracts and stripped.startswith("///"):
            indent = len(lines[start]) - len(lines[start].lstrip())
            for line in lines[i:i + 30]:
                if line.strip() and len(line) - len(line.lstrip()) == indent and not line.strip().startswith("["):
                    outer, kind = around(indent)
                    declared = DECLARED.match(line)
                    head = line.split("(", 1)[0]
                    mine = public_type(declared) if declared else (
                        opens(head) or (kind in ("interface", "enum") and not re.search(r"\b(?:private|internal)\b", head)))
                    public = outer and mine
                    break
        text = sum(1 for line in lines[start:i] if not TAG_ONLY.match(line.strip()))
        blocks.append((start + 1, i, text, public))
    return blocks


def public_partials(repo):
    """Partial types declared public in some file: a part elsewhere may leave the modifier out."""
    names = set()
    for path in repo.under(("src/", "samples/"), (".cs",)):
        for match in re.finditer(DECLARED.pattern, repo.read(path), re.M):
            if "partial" in match.group(2).split() and opens(match.group(2)):
                names.add(match.group(4))
    return names


def comments(repo):
    """Comments are documents next to the code (docs/development.md section 11): none that excuse, narrate the past, or
    run longer than what they are for needs."""
    partials = public_partials(repo)
    for path in repo.under(CODE, (".cs",)):
        text = repo.read(path)
        lines = text.split("\n")
        for number, line in enumerate(lines, 1):
            if EXCUSE.search(line):
                repo.fail("comments", f"{path}:{number}", "an excuse comment (TODO, FIXME, HACK). Do it now, or record why it "
                          "is not done as a decision (.agents/skills/record-decision); later changes copy what they find.")
        for first, last, length, public in comment_blocks(text, partials, contracts=not path.startswith("tests/")):
            for number in range(first, last + 1):
                said = HISTORY.search(lines[number - 1])
                if said:
                    repo.fail("comments", f"{path}:{number}", f"a comment that narrates the past ('{said.group(0)}'). Say what "
                              "holds now, or nothing: what the code did before and what was fixed are the commit message's, and "
                              "the test guards it (docs/development.md section 11).")
                    break
            limit = CONTRACT_LINES if public else NOTE_LINES
            if length > limit:
                repo.fail("comments", f"{path}:{first}", (
                    f"a {length}-line contract, the limit is {limit}. Keep what a caller outside the assembly relies on (what it "
                    "promises, its preconditions, how it fails); delete how it is implemented, why, and what it used to do"
                    if public else
                    f"a {length}-line comment in internal code or a test, the limit is {limit}. Delete what the code, the names "
                    "or the test's name already say, and any history; keep only the constraint a reader would otherwise break, "
                    "in a line or two. A longer reason is a decision (.agents/skills/record-decision)")
                    + " (docs/development.md section 11).")


def comment_growth(repo, base):
    """Not a check: notes a change whose comments in internal code and tests grow faster than its code (lines of text,
    counted in each changed file before and after, so moving or rewrapping a comment is not growth)."""
    changed = repo.git("diff", "--name-only", "-z", base, "--", "*.cs") if base else None
    if changed is None:
        return
    paths = [p for p in changed.split("\0") if p.startswith(CODE)]
    paths += [p for p in (repo.git("ls-files", "--others", "--exclude-standard", "-z", "--", "*.cs") or "").split("\0") if p.startswith(CODE)]
    partials, notes, code = public_partials(repo), 0, 0

    def count(text, path):
        lines = text.split("\n")
        commented, internal = set(), 0
        for first, last, length, public in comment_blocks(text, partials, contracts=not path.startswith("tests/")):
            commented.update(range(first, last + 1))
            internal += 0 if public else length
        return internal, sum(1 for n, line in enumerate(lines, 1)
                             if n not in commented and line.strip() not in ("", "{", "}", "};", "});"))

    for path in paths:
        before = count(repo.git("show", f"{base}:{path}") or "", path)
        after = count(repo.read(path), path)
        notes, code = notes + after[0] - before[0], code + after[1] - before[1]
    if notes >= 10 and notes > NOTE_SHARE * max(code, 0):
        repo.notes.append(f"comments: +{notes} lines of comments in internal code and tests for {code:+} lines of code since "
                          f"{base[:9]}. The reader has the code: keep a comment only for what reading it would not tell, in a "
                          "line or two (docs/development.md section 11).")


def sleeps(repo):
    """Tests decide when a call returns; they do not wait for the clock. The count only goes down."""
    count = sum(len(SLEEP.findall(line.split("//", 1)[0]))   # a comment that names the call is not a call
                for p in repo.under(("tests/",), (".cs",)) for line in repo.read(p).split("\n"))
    if count > SLEEP_BASELINE:
        repo.fail("sleeps", "tests/", f"{count} Thread.Sleep/Task.Delay calls, the baseline is {SLEEP_BASELINE}. Find the new "
                  "one with: git diff <base> -- tests | grep -E '^\\+.*(Thread\\.Sleep|Task\\.Delay)\\('. Let the test open a "
                  "gate or advance a clock it owns instead of waiting (AGENTS.md section 4).")
    elif count < SLEEP_BASELINE:
        repo.notes.append(f"sleeps: {count} now, the baseline is {SLEEP_BASELINE}: lower SLEEP_BASELINE in scripts/gate.py "
                          "(a commit of its own).")


def names(repo):
    """What a document puts in backticks is in the repository: a path that exists, a name some file other than a document uses."""
    known = set(EXTERNAL)
    for path in repo.paths:
        if not path.startswith("docs/") and not path.endswith(".md") and path.endswith(TEXT + ("BannedSymbols.txt",)):
            text = repo.read(path)
            if path == "scripts/gate.py":
                text = text.split("\ndef selftest", 1)[0]   # the names its self-test makes up exist nowhere
            known.update(WORD.findall(text))
    documents = [p for p in repo.paths if p.endswith(".md") and (p.startswith(("docs/", ".agents/")) or "/" not in p)]
    for doc in documents:
        text = re.sub(r"^```.*?^```", lambda m: "\n" * m.group(0).count("\n"), repo.read(doc), flags=re.S | re.M)
        reported = set()
        for number, line in enumerate(text.split("\n"), 1):
            for token in re.findall(r"`([^`\n]+)`", line):
                if PATH.match(token) and not re.search(r"[*<>]", token):
                    missing = [] if os.path.exists(os.path.join(repo.root, token.rstrip("/"))) else [token]
                else:
                    missing = [w for w in WORD.findall(token) if w not in known and any(p.match(w) for p in POINTER)]
                for name in missing:
                    if name not in reported:
                        reported.add(name)
                        repo.fail("names", f"{doc}:{number}", f"`{name}` is not in the repository. Renamed: use the new name. "
                                  "Removed, or only imagined: write it without backticks (backticks promise that grep finds it).")


def layers(repo):
    """Project references point up the table of docs/architecture.md only; friend assemblies do not multiply."""
    order = re.findall(r"^\| `(StrataRec\.[A-Za-z.]+)` \|", repo.read("docs/architecture.md"), re.M)
    for path in repo.under(("src/",), (".csproj",)):
        project = os.path.basename(path)[:-len(".csproj")]
        if project not in order:
            repo.fail("layers", path, "the project is not in the table of docs/architecture.md: add a row where it belongs "
                      "(it may reference only the rows above it).")
            continue
        try:   # as XML: an attribute on the next line, or in another order, is the same reference
            items = [(e.tag.rsplit("}", 1)[-1], e.get("Include") or "") for e in ET.fromstring(repo.read(path)).iter()]
        except ET.ParseError as error:
            repo.fail("layers", path, f"is not XML the gate can read ({error}); its references were not checked.")
            continue
        for target in [os.path.basename(i.replace("\\", "/"))[:-len(".csproj")] for t, i in items if t == "ProjectReference"]:
            if target not in order or order.index(target) >= order.index(project):
                repo.fail("layers", path, f"references {target}, which is not above {project} in the table of "
                          "docs/architecture.md. Dependencies point up the table only: move the code to where the knowledge "
                          "lives, or put the contract in the lower project.")
        for friend in [i[len("StrataRec."):] for t, i in items if t == "InternalsVisibleTo" and i.startswith("StrataRec.")]:
            pair = (project[len("StrataRec."):], friend)
            if not friend.endswith(("Tests", "Benchmarks")) and pair not in FRIENDS:
                repo.fail("layers", path, f"{friend} is a new friend assembly of {project}. Make what it needs public with "
                          "a contract, or move the code into the assembly that owns the knowledge.")


def terms(repo):
    """Public type names do not use the words docs/glossary.md bans (one concept, one word)."""
    banned = set()
    for words in re.findall(r"禁：([A-Za-z]+(?:、[A-Za-z]+)*)", repo.read("docs/glossary.md")):
        banned.update(w.lower() for w in words.split("、"))
    for path in repo.under(("src/",), (".cs",)):
        for match in PUBLIC_TYPE.finditer(repo.read(path)):
            name = match.group(1) or match.group(2)
            for hump in re.findall(r"[A-Z][a-z0-9]*", name):
                word = hump.lower()
                if word in banned or (word.endswith("s") and word[:-1] in banned):
                    repo.fail("terms", path, f"public type {name} uses '{hump}', a word docs/glossary.md bans. "
                              "Use the glossary's word for the concept; a new concept gets a row in the glossary first.")


def commits(repo, base):
    """A commit changes the rules or the code, not both (AGENTS.md section 6)."""
    log = repo.git("log", "--no-merges", "--pretty=format:%h", f"{base}..HEAD") if base else None
    if log is None:
        repo.notes.append("commits: no base to compare with (pass --base <commit>); the rule-file check did not run.")
        return
    for commit in log.split():
        files = (repo.git("diff-tree", "--no-commit-id", "--name-only", "-r", "-z", commit) or "").split("\0")
        rules = [f for f in files if any(f.startswith(r) if r.endswith("/") else f == r for r in RULES)]
        code = [f for f in files if f.startswith(CODE)]
        if rules and code:
            repo.fail("commits", commit, f"changes rule files ({rules[0]}) together with code ({code[0]}). Split it: when a "
                      "check is in the way, fix the code; when the rule is wrong, change it in a commit of its own that says why.")


def size(repo, base):
    """Not a check: tells the author when a change is larger than nearly all changes here."""
    stat = repo.git("diff", "--numstat", base) if base else None
    if stat is None:
        return
    changed = [line.split("\t") for line in stat.strip().split("\n") if line]
    for path in (repo.git("ls-files", "--others", "--exclude-standard", "-z") or "").split("\0"):   # not added yet
        if path.startswith(CODE):
            changed.append((str(len(repo.read(path).splitlines())), "0", path))
    projects, added, tested = set(), 0, 0
    for plus, _, path in changed:
        if plus == "-":
            continue
        if path.startswith("src/"):
            projects.add(path.split("/")[1])
            added += int(plus)
        elif path.startswith("tests/"):
            tested += int(plus)
    repo.notes.append(f"size: {len(projects)} src projects, +{added} src lines, +{tested} test lines since {base[:9]}.")
    if len(projects) >= SIZE_PROJECTS or added >= SIZE_LINES:
        repo.notes.append("size: larger than nineteen in twenty changes here. If one sub-requirement or a rare case drives it, stop "
                          "and lay out the cost and a smaller cut (AGENTS.md section 5).")


def pages(repo, base):
    """Not a check: names the pages a change made longer, so that each addition is decided and not a habit."""
    stat = repo.git("diff", "--numstat", "--no-renames", base, "--", *PAGES) if base else None
    if stat is None:
        return
    grown = []
    for line in stat.strip().split("\n"):
        plus, minus, path = line.split("\t") if line else ("-", "-", "")
        if plus != "-" and not path.startswith("docs/decisions/") and int(plus) > int(minus):
            grown.append(f"{path} +{int(plus) - int(minus)}")
    for path in (repo.git("ls-files", "--others", "--exclude-standard", "-z", "--", *PAGES) or "").split("\0"):   # not added yet
        if path and not path.startswith("docs/decisions/"):
            grown.append(f"{path} +{len(repo.read(path).splitlines())}")
    if grown:
        repo.notes.append(f"docs: longer since {base[:9]} (lines): {', '.join(sorted(grown))}. A fix, a small feature or a new option "
                          "is no reason to touch a page: keep what the code cannot say and its readers need, delete the rest "
                          "(.agents/skills/edit-docs).")


def slow(directory):
    """The slowest tests of a run. A test that waits out a timeout is a defect of the feedback loop, not a cost of doing business."""
    tests = []
    for report in sorted(glob.glob(os.path.join(directory, "*.trx"))):
        for element in ET.parse(report).getroot().iter():
            if element.tag.endswith("UnitTestResult") and element.get("duration"):
                h, m, s = element.get("duration").split(":")
                tests.append((int(h) * 3600 + int(m) * 60 + float(s), element.get("testName")))
    if not tests:
        print(f"gate: slow: no test reports in {directory} (run scripts/check.sh)")
        return 1
    tests.sort(reverse=True)
    print(f"slow: {len(tests)} tests, {sum(t for t, _ in tests):.0f} s of test time; over {SLOW_NOTE_S:g} s:")
    for seconds, name in [t for t in tests if t[0] >= SLOW_NOTE_S][:12]:
        print(f"  {seconds:6.2f} s  {name}")
    worst = [t for t in tests if t[0] >= SLOW_FAIL_S]
    for seconds, name in worst:
        print(f"gate: slow: {name}: {seconds:.1f} s, the limit for one test is {SLOW_FAIL_S:g} s. See what it waits for: a "
              "timeout it lets run out, a gate opened too late, an input larger than the property needs.")
    return 1 if worst else 0


def run(repo, base):
    for check in (budgets, comments, sleeps, names, layers, terms):
        check(repo)
    commits(repo, base)
    return repo


def resolve_base(repo, given):
    """The commit the change is compared with: the one asked for, or where the branch left origin/main."""
    if given and set(given) != {"0"}:   # all zeros: the "before" of a branch's first push, which names no commit
        found = repo.git("merge-base", given, "HEAD")
        if not found:
            raise SystemExit(f"gate: --base {given}: not a commit that shares history with HEAD (in a shallow clone, fetch it first).")
        return found.strip()
    found = repo.git("merge-base", "origin/main", "HEAD")
    return found.strip() if found else None


def selftest():
    """Builds a small repository that passes, then breaks one rule at a time and expects exactly that check to say so."""
    def write(root, path, text):
        os.makedirs(os.path.dirname(os.path.join(root, path)) or root, exist_ok=True)
        with open(os.path.join(root, path), "w", encoding="utf-8") as f:
            f.write(text)

    clean = {
        "AGENTS.md": "# rules\n\nSee `docs/architecture.md` and `CandidateTable`.\n",
        "docs/architecture.md": "| `StrataRec.Core` | a | |\n| `StrataRec.Pipeline` | b | Core |\n",
        "docs/glossary.md": "| 节点 / node | x | 禁：stage、bucket。不叫 step |\n",
        "docs/decisions/execution.md": "- **x.** `timeout_ms` and `COLUMN_NO_PRODUCER`.\n\n```sh\n`NotChecked` in a fence\n```\n",
        ".agents/skills/x/SKILL.md": "---\nname: x\n---\n",
        "src/StrataRec.Core/StrataRec.Core.csproj": '<Project><ItemGroup><InternalsVisibleTo Include="StrataRec.Pipeline" />'
                                                    '<InternalsVisibleTo Include="StrataRec.UnitTests" /></ItemGroup></Project>',
        "src/StrataRec.Core/CandidateTable.cs": "public sealed class CandidateTable { }\n"
                                                'class C { string a = "timeout_ms", b = "COLUMN_NO_PRODUCER", c = "TODO"; }\n',
        "src/StrataRec.Core/名字.cs": "public sealed class Named { }\n",
        # A contract of public API may be longer than a note; so may one on a part of a partial type that is public in
        # another file. A raw string's lines are not comments; "used to" describing a purpose is not the past.
        "src/StrataRec.Core/Contract.cs": "namespace X;\n\npublic sealed class Api\n{\n" + "    /// contract\n" * CONTRACT_LINES
            + "    public void Run() { }\n\n    internal sealed class Inner\n    {\n" + "        // note\n" * NOTE_LINES
            + "        public void Go() { }\n\n        /// <summary>\n" + "        /// note\n" * NOTE_LINES
            + "        /// </summary>\n        public void Stop() { }\n    }\n}\n\npublic partial class Split { }\n",
        "src/StrataRec.Core/Split.cs": "namespace X;\n\npartial class Split\n{\n" + "    /// contract\n" * CONTRACT_LINES
            + "    public int Size { get; }\n\n    // The key used to find the record; a clock that starts at 2026-01-01.\n}\n",
        "tests/T/ConfigTests.cs": 'var config = """\n' + "  // a JSONC comment\n" * (NOTE_LINES + 2) + '  {}\n  """;\n',
        "src/StrataRec.Pipeline/StrataRec.Pipeline.csproj": '<Project><ItemGroup><ProjectReference Include="..\\StrataRec.Core\\'
                                                            'StrataRec.Core.csproj" /></ItemGroup></Project>',
        "tests/T/WaitTests.cs": "await Task.Delay(Timeout.Infinite, token);\n" + "// was Thread.Sleep(50)\nint x;\n" * (SLEEP_BASELINE + 1),
    }
    breaks = [
        ("budget", "AGENTS.md", clean["AGENTS.md"] + "x\n" * AGENTS_LINES),
        ("budget", ".agents/skills/x/SKILL.md", "x\n" * (SKILL_LINES + 1)),
        ("budget", "docs/decisions/execution.md", "x" * (DECISION_CHARS + 1)),
        ("comments", "src/StrataRec.Core/Later.cs", "// TODO: handle the empty table\n"),
        ("comments", "src/StrataRec.Core/名字.cs", "/// <summary>HACK around the pool.</summary>\n"),
        ("comments", "src/StrataRec.Core/Note.cs", "class N\n{\n" + "    // a note\n" * (NOTE_LINES + 1) + "    void M() { }\n}\n"),
        ("comments", "src/StrataRec.Core/Hidden.cs", "internal sealed class H\n{\n" + "    /// a note\n" * (NOTE_LINES + 1)
                                                     + "    public void M() { }\n}\n"),
        ("comments", "src/StrataRec.Core/Long.cs", "public sealed class L\n{\n" + "    /// contract\n" * (CONTRACT_LINES + 1)
                                                   + "    public void M() { }\n}\n"),
        ("comments", "tests/T/StoryTests.cs", "public sealed class S\n{\n" + "    /// the scenario\n" * (NOTE_LINES + 1)
                                              + "    [Fact]\n    public void Works() { }\n}\n"),
        ("comments", "src/StrataRec.Core/Past.cs", "class P\n{\n    // A poison batch used to be retried forever.\n    void M() { }\n}\n"),
        ("comments", "src/StrataRec.Core/Dated.cs", "class D\n{\n    // (2026-11-22: the client's error)\n    void M() { }\n}\n"),
        ("sleeps", "tests/T/SleepTests.cs", "Thread.Sleep (50);\n" * (SLEEP_BASELINE + 1)),
        ("names", "docs/decisions/execution.md", "- **x.** `RemovedType` did this.\n"),
        ("names", "docs/decisions/execution.md", "- **x.** the key `removed_key`.\n"),
        ("names", "AGENTS.md", "See `docs/gone.md`.\n"),
        ("layers", "src/StrataRec.Core/StrataRec.Core.csproj", '<Project><ItemGroup><ProjectReference\n  PrivateAssets="all"\n'
                                                               '  Include="..\\StrataRec.Pipeline\\StrataRec.Pipeline.csproj" /></ItemGroup></Project>'),
        ("layers", "src/StrataRec.Core/StrataRec.Core.csproj", "<Project><ItemGroup>"),
        ("layers", "src/StrataRec.New/StrataRec.New.csproj", "<Project />"),
        ("layers", "src/StrataRec.Pipeline/StrataRec.Pipeline.csproj", '<Project><ItemGroup><InternalsVisibleTo Include="StrataRec.Core" /></ItemGroup></Project>'),
        ("terms", "src/StrataRec.Core/Stage.cs", "public sealed class RankingStage { }\n"),
        ("terms", "src/StrataRec.Core/Bucket.cs", "    public readonly record struct BucketId(int Value);\n"),
        ("terms", "src/StrataRec.Core/Buckets.cs", "public delegate int CandidateBuckets<T>(T rows);\n"),
    ]
    problems = []
    with tempfile.TemporaryDirectory() as root:
        for path, text in clean.items():
            write(root, path, text)
        passed = run(Repo(root), None)
        if passed.failures:
            problems.append("the clean repository fails:\n  " + "\n  ".join(passed.failures))
        for check, path, text in breaks:
            before = clean.get(path)
            write(root, path, text)
            found = run(Repo(root), None).failures
            if len(found) != 1 or not found[0].startswith(f"gate: {check}: "):
                problems.append(f"breaking {path} for '{check}' gave: {found or 'nothing'}")
            if before is None:
                os.remove(os.path.join(root, path))
            else:
                write(root, path, before)
    with tempfile.TemporaryDirectory() as root:   # the rule-file check needs history
        def git(*args):
            subprocess.run(("git", "-C", root, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false") + args,
                           check=True, capture_output=True)
        git("init", "-q")
        for message, paths in (("base", ["README.md"]), ("rules", ["scripts/gate.py", "docs/a.md"]), ("code", ["src/a.cs", "docs/a.md"]),
                               ("alike", ["AGENTS.md.backup", "docs/x@1.md", "tests/b.cs"]), ("both", ["Directory.Build.props", "tests/a.cs"])):
            for path in paths:
                write(root, path, message)
            git("add", "-A")
            git("commit", "-q", "-m", message)
        repo = Repo(root)
        commits(repo, repo.git("rev-list", "--max-parents=0", "HEAD").strip())
        if len(repo.failures) != 1 or "Directory.Build.props" not in repo.failures[0]:
            problems.append(f"the rule-file check gave: {repo.failures or 'nothing'}")
        write(root, "README.md", "base\nand how the new option is implemented\n")
        write(root, "docs/decisions/a.md", "- a decision\n")
        pages(repo, repo.git("rev-parse", "HEAD").strip())
        if len(repo.notes) != 1 or "README.md +1" not in repo.notes[0] or "decisions" in repo.notes[0]:
            problems.append(f"the note on longer pages gave: {repo.notes or 'nothing'}")
        for comments_per_line, expected in ((0, 0), (1, 1)):
            write(root, "src/Grow.cs", "class G\n{\n" + ("    // step\n" * comments_per_line + "    int a;\n") * 12 + "}\n")
            repo.notes = []
            comment_growth(repo, repo.git("rev-parse", "HEAD").strip())
            if len(repo.notes) != expected or (expected and "+12 lines of comments" not in repo.notes[0]):
                problems.append(f"the note on growing comments gave: {repo.notes or 'nothing'} for {comments_per_line} comment(s) a line")
    with tempfile.TemporaryDirectory() as root:
        row = '<UnitTestResult testName="{}" duration="00:00:{}" outcome="Passed" />'
        for name, seconds, expected in (("fast", "00.5000000", 0), ("slow", f"{SLOW_FAIL_S + 1:010.7f}", 1)):
            write(root, "a.trx", f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{row.format(name, seconds)}</Results></TestRun>')
            stdout, sys.stdout = sys.stdout, open(os.devnull, "w")
            try:
                code = slow(root)
            finally:
                sys.stdout.close()
                sys.stdout = stdout
            if code != expected:
                problems.append(f"the slow-test check returned {code} for a {seconds} s test")
    for problem in problems:
        print("gate: selftest:", problem)
    print("selftest: " + ("FAILED" if problems else f"every check bites ({len(breaks) + 6} cases)"))
    return 1 if problems else 0


def main(argv):
    if argv[:1] == ["selftest"]:
        return selftest()
    if argv[:1] == ["slow"]:
        return slow(argv[1] if len(argv) > 1 else "TestResults")
    if argv and (len(argv) != 2 or argv[0] != "--base"):
        raise SystemExit("gate: usage: gate.py [--base <commit>] | slow <directory> | selftest")
    repo = Repo(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    base = resolve_base(repo, argv[1] if argv else os.environ.get("GATE_BASE"))
    run(repo, base)
    size(repo, base)
    pages(repo, base)
    comment_growth(repo, base)
    for line in repo.failures + repo.notes:
        print(line)
    print(f"gate: {len(repo.failures)} failure(s)" if repo.failures else "gate: ok")
    return 1 if repo.failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
