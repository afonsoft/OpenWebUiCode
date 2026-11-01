#!/usr/bin/env python3
"""Guard: form accessibility in .razor files.

Checks, per file:
1. Every `for="X"` resolves to an `id="X"` in the same file.
2. No duplicate `id="..."` literals in a file.
3. Every <input>, <select>, <textarea> has an accessible name source:
   - aria-label / aria-labelledby, or
   - id referenced by a `for=`, or
   - wrapped inside a <label> element, or
   - type="hidden".
4. No <label for> / id pair where the id appears inside a @foreach/@for loop
   while the label is outside it (duplicate-id risk).

Exits non-zero and prints violations.
"""
import re
import sys
import glob

FORM_TAGS = re.compile(r'<(input|select|textarea)\b')
ATTR = re.compile(r'(\w[\w-]*)\s*=\s*"([^"]*)"')
LABEL_OPEN = re.compile(r'<label\b[^>]*>')
LABEL_CLOSE = re.compile(r'</label>')
LOOP_OPEN = re.compile(r'@(?:foreach|for)\b[^{]*\{')
BRACE_OPEN = re.compile(r'[({\[]')
BRACE_CLOSE = re.compile(r'[)}\]]')


def tag_attrs(src, start):
    """Return (attrs_str, end_index_of_'>'), quote-aware."""
    i = start
    q = None
    while i < len(src):
        c = src[i]
        if q:
            if c == q:
                q = None
        elif c in '"\'':
            q = c
        elif c == '>':
            return src[start:i], i
        i += 1
    return src[start:], len(src)


def brace_spans(src, start):
    """Given index of '{', return (start,end) span of balanced braces."""
    depth = 0
    i = start
    q = None
    while i < len(src):
        c = src[i]
        if q:
            if c == q:
                q = None
        elif c in '"\'':
            q = c
        elif c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return (start, i)
        i += 1
    return (start, len(src))


def loop_spans(src):
    spans = []
    for m in LOOP_OPEN.finditer(src):
        b = src.find('{', m.end() - 1)
        if b != -1:
            spans.append(brace_spans(src, b))
    return spans


def in_spans(pos, spans):
    return any(a <= pos <= b for a, b in spans)


def label_ranges(src):
    """Return list of (start,end) ranges covering <label ..>...</label>."""
    ranges = []
    stack = []
    for m in re.finditer(r'<label\b[^>]*>|</label>', src):
        if m.group(0).startswith('</'):
            if stack:
                ranges.append((stack.pop(), m.end()))
        else:
            stack.append(m.start())
    return ranges


def check_for_id(f, ids, fors):
    """1. for -> id"""
    return [f'{f}: for="{fr}" has no matching id' for fr in fors if fr not in ids]


def check_dup_ids(f, ids):
    """2. duplicate ids (literal duplicates only)"""
    seen = {}
    for i in ids:
        seen[i] = seen.get(i, 0) + 1
    return [f'{f}: duplicate id "{i}" x{c}' for i, c in seen.items() if c > 1]


def check_control_names(f, src, fors, lranges):
    """3. control accessible names"""
    errors = []
    for m in FORM_TAGS.finditer(src):
        attrs, _ = tag_attrs(src, m.end())
        a = dict(ATTR.findall(attrs))
        if a.get('type') == 'hidden':
            continue
        if 'aria-label' in a or 'aria-labelledby' in a:
            continue
        cid = a.get('id')
        if cid and cid in fors:
            continue
        if in_spans(m.start(), lranges):
            continue
        # placeholder alone is NOT a name
        line = src[:m.start()].count('\n') + 1
        errors.append(f"{f}:{line}: <{m.group(1)}> without accessible name")
    return errors


def check_loop_labels(f, src, spans):
    """4. label-outside / control-inside loop"""
    errors = []
    for m in re.finditer(r'\bid="([A-Za-z0-9_-]+)"', src):
        if in_spans(m.start(), spans):
            # is some for= pointing here defined outside the loop?
            for fm in re.finditer(r'\bfor="' + re.escape(m.group(1)) + r'"', src):
                if not in_spans(fm.start(), spans):
                    line = src[:m.start()].count('\n') + 1
                    errors.append(
                        f"{f}:{line}: id \"{m.group(1)}\" inside loop referenced by label outside loop")
    return errors


def check_file(f):
    src = open(f, encoding='utf-8').read()
    ids = re.findall(r'\bid="([A-Za-z0-9_-]+)"', src)
    fors = re.findall(r'\bfor="([A-Za-z0-9_-]+)"', src)
    spans = loop_spans(src)
    lranges = label_ranges(src)
    return (
        check_for_id(f, ids, fors)
        + check_dup_ids(f, ids)
        + check_control_names(f, src, fors, lranges)
        + check_loop_labels(f, src, spans)
    )


def main():
    errors = []
    files = sorted(glob.glob('src/OpenWebUI.Client/**/*.razor', recursive=True))
    for f in files:
        errors.extend(check_file(f))

    if errors:
        print("Form a11y violations:")
        for e in errors:
            print("  -", e)
        sys.exit(1)
    print(f"OK: {len(files)} razor files checked")


if __name__ == '__main__':
    main()
