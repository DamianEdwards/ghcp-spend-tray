"""Reversible, comment-preserving edits to an existing Waybar JSONC config."""

import json
import glob
import os
from pathlib import Path
import re

MODULE = "custom/ghcp-spend-tray"
TOKEN = re.compile(r'\s+|//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|[{}\[\]:,]|[^{}\[\]:,\s]+')


class Document:
    def __init__(self, source):
        self.source = source
        self.tokens = [match for match in TOKEN.finditer(source)
                       if not match[0].isspace() and not match[0].startswith(("//", "/*"))]
        self.position = 0
        try:
            self.root = self.read()
        except IndexError as error:
            raise ValueError("Incomplete Waybar configuration.") from error
        if self.position != len(self.tokens):
            raise ValueError("Unexpected content after Waybar configuration.")

    def read(self):
        token = self.tokens[self.position]
        self.position += 1
        start = token.start()
        if token[0] not in ("{", "["):
            return {"start": start, "end": token.end(), "value": json.loads(token[0])}
        is_object = token[0] == "{"
        close = "}" if is_object else "]"
        children = {} if is_object else []
        while self.tokens[self.position][0] != close:
            if is_object:
                key = json.loads(self.tokens[self.position][0])
                if not isinstance(key, str) or key in children:
                    raise ValueError("Invalid or duplicate Waybar property.")
                self.position += 1
                if self.tokens[self.position][0] != ":":
                    raise ValueError("Expected a colon in Waybar configuration.")
                self.position += 1
                children[key] = self.read()
            else:
                children.append(self.read())
            if self.tokens[self.position][0] != ",":
                break
            self.position += 1
        token = self.tokens[self.position]
        self.position += 1
        if token[0] != close:
            raise ValueError("Invalid Waybar configuration.")
        value = ({key: node["value"] for key, node in children.items()} if is_object
                 else [node["value"] for node in children])
        return {"start": start, "end": token.end(), "value": value, "children": children}

    def bar(self, index):
        node = self.root
        if isinstance(node["value"], list):
            if index is None and len(node["value"]) != 1:
                raise ValueError("Multiple Waybar bars: select one with --waybar-bar INDEX (zero-based).")
            if (index or 0) < 0 or (index or 0) >= len(node["value"]):
                raise ValueError("Waybar bar index is out of range.")
            node = node["children"][index or 0]
        elif index not in (None, 0):
            raise ValueError("This Waybar config contains only one bar.")
        if not isinstance(node["value"], dict):
            raise ValueError("A Waybar bar must be an object.")
        return node


def set_field(source, index, key, value):
    document = Document(source)
    bar = document.bar(index)
    node = bar["children"].get(key)
    encoded = json.dumps(value, ensure_ascii=True)
    if node:
        return source[:node["start"]] + encoded + source[node["end"]:]
    position = bar["start"] + 1
    addition = "\n  " + json.dumps(key) + ": " + encoded + ("," if bar["value"] else "") + "\n"
    return source[:position] + addition + source[position:]


def remove_field(source, index, key):
    document = Document(source)
    bar = document.bar(index)
    node = bar["children"].get(key)
    if not node:
        return source
    tokens = document.tokens
    value_start = next(i for i, token in enumerate(tokens) if token.start() == node["start"])
    start = tokens[value_start - 2].start()
    end = node["end"]
    following = next(i for i, token in enumerate(tokens) if token.start() >= end)
    if tokens[following][0] == ",":
        end = tokens[following].end()
    elif tokens[value_start - 3][0] == ",":
        start = tokens[value_start - 3].start()
    return source[:start] + source[end:]


def effective(bar, path, seen=()):
    if path in seen:
        raise ValueError("Cyclic Waybar includes.")
    result = dict(bar)
    includes = bar.get("include", [])
    if isinstance(includes, str):
        includes = [includes]
    if not isinstance(includes, list) or not all(isinstance(item, str) for item in includes):
        raise ValueError("Waybar include must be a path or a list of paths.")
    for item in includes:
        included = Path(os.path.expandvars(os.path.expanduser(item)))
        if not included.is_absolute():
            raise ValueError("Use absolute, ~/ or environment-expanded Waybar includes before integrating; "
                             "relative includes depend on Waybar's launch directory and search path.")
        matches = sorted(glob.glob(str(included)))
        if not matches:
            raise ValueError(f"Waybar include not found: {included}")
        for match in matches:
            included = Path(match).resolve()
            content = Document(included.read_text()).bar(None)["value"]
            for key, value in effective(content, included, (*seen, path)).items():
                result.setdefault(key, value)
    return result


def remove(source, state):
    if source == state["installed"]:
        return state["original"]
    if state.get("kind") == "tray":
        if not state["added"]:
            return source
        index = state["bar"]
        values = Document(source).bar(index)["value"].get("modules-right")
        if not isinstance(values, list) or values.count("tray") > 1:
            raise ValueError("The installed tray placement changed ambiguously; restore it before removal.")
        remaining = [value for value in values if value != "tray"]
        if not state["had_modules_right"] and remaining == state["previous_modules"]:
            return remove_field(source, index, "modules-right")
        return set_field(source, index, "modules-right", remaining)
    index = state["bar"]
    bar = Document(source).bar(index)["value"]
    if MODULE in bar and bar[MODULE] != state["module"]:
        raise ValueError("The installed GHCPSpendTray Waybar module was edited; remove it manually before uninstalling.")
    source = remove_field(source, index, MODULE)
    for key in ("modules-left", "modules-center", "modules-right"):
        values = Document(source).bar(index)["value"].get(key)
        if isinstance(values, list) and MODULE in values:
            remaining = [value for value in values if value != MODULE]
            if key == "modules-right" and not state["had_modules_right"] and remaining == state["previous_modules"]:
                source = remove_field(source, index, key)
            else:
                source = set_field(source, index, key, remaining)
    Document(source)
    return source


def prepare(path, index, module, previous=None):
    path = path.absolute()
    if path.is_symlink():
        raise ValueError(f"Waybar config is a symlink; pass its actual file path: {path.resolve()}")
    source = path.read_text()
    if previous:
        if previous["path"] != str(path) or previous["bar"] != index:
            raise ValueError("Uninstall the old Waybar integration before selecting a different config/bar.")
        source = remove(source, previous)
    bar = Document(source).bar(index)["value"]
    merged = effective(bar, path.resolve())
    if MODULE in merged or any(MODULE in merged.get(key, []) for key in
                              ("modules-left", "modules-center", "modules-right")):
        raise ValueError("A GHCPSpendTray Waybar module already exists outside this installation.")
    modules = merged.get("modules-right", [])
    if not isinstance(modules, list) or not all(isinstance(value, str) for value in modules):
        raise ValueError("Waybar modules-right must be a list of module names.")
    installed = set_field(set_field(source, index, MODULE, module), index, "modules-right", [*modules, MODULE])
    Document(installed)
    return {"path": str(path), "bar": index, "original": source, "installed": installed,
            "module": module, "had_modules_right": "modules-right" in bar, "previous_modules": modules}


def prepare_tray(path, index, previous=None):
    path = path.absolute()
    if path.is_symlink():
        raise ValueError(f"Waybar config is a symlink; pass its actual file path: {path.resolve()}")
    source = path.read_text()
    if previous:
        if previous["path"] != str(path) or previous["bar"] != index:
            raise ValueError("Uninstall the old Waybar integration before selecting a different config/bar.")
        source = remove(source, previous)
    bar = Document(source).bar(index)["value"]
    merged = effective(bar, path.resolve())
    lists = [merged.get(key, []) for key in ("modules-left", "modules-center", "modules-right")]
    if any(not isinstance(values, list) or not all(isinstance(value, str) for value in values) for values in lists):
        raise ValueError("Waybar module lists must contain module names.")
    if MODULE in merged or any(MODULE in values for values in lists):
        raise ValueError("An unowned GHCPSpendTray custom module already exists; remove it before native tray integration.")
    added = not any(value == "tray" or value.startswith("tray#") for values in lists for value in values)
    modules = merged.get("modules-right", [])
    installed = set_field(source, index, "modules-right", [*modules, "tray"]) if added else source
    return {"kind": "tray", "path": str(path), "bar": index, "original": source, "installed": installed,
            "added": added, "had_modules_right": "modules-right" in bar, "previous_modules": modules}
