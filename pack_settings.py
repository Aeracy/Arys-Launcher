import json
import math
import re


KEY_BINDING = re.compile(r"(?:key\.(?:keyboard|mouse)\.[a-z0-9_.-]+|scancode\.\d+)(?::(?:SHIFT|CONTROL|ALT))*\Z")
SETTING_NAME = re.compile(r"[A-Za-z0-9_.:-]+\Z")


def is_arys_configuration(path):
    parts = path.replace("\\", "/").casefold().strip("/").split("/")
    return any(part in ("config", "defaultconfigs", "serverconfig") and parts[index + 1].startswith("arys")
               for index, part in enumerate(parts[:-1]))


def is_pack_name(name):
    if not isinstance(name, str) or len(name) > 256:
        return False
    if name.startswith("mod/"):
        return bool(re.fullmatch(r"mod/[a-z0-9_.-]+:[a-z0-9_.-]+", name))
    filename = name.removeprefix("file/")
    return bool(filename) and not any(character in filename for character in "\\/:\r\n") and filename not in (".", "..")


def is_game_option(key, value, rules):
    options = rules["GameOptions"]
    if key not in options["Keys"] and key not in options["Defaults"] and not key.startswith(tuple(options["Prefixes"])):
        return False
    if len(value) > 16384 or any(ord(character) < 32 for character in value):
        return False
    if key.startswith("key_"):
        return bool(SETTING_NAME.fullmatch(key) and KEY_BINDING.fullmatch(value))
    if key in ("lang", "tutorialStep"):
        return bool(re.fullmatch(r"[a-z0-9_]+", value))
    try:
        parsed = json.loads(value)
    except (ValueError, RecursionError):
        return False
    if key in ("resourcePacks", "incompatibleResourcePacks"):
        return isinstance(parsed, list) and len(parsed) <= 256 and all(is_pack_name(name) for name in parsed)
    if isinstance(parsed, (bool, int)):
        return True
    if isinstance(parsed, float):
        return math.isfinite(parsed)
    return isinstance(parsed, str) and len(parsed) <= 128 and not any(c in parsed for c in ":/\\\r\n")


def read_game_options(text, rules, strict=False):
    if len(text.encode("utf-8")) > 65536:
        raise ValueError("The game options file exceeds 64 KiB.")
    result = {}
    for line in text.removeprefix("\ufeff").splitlines():
        if not line.strip():
            continue
        key, separator, value = line.partition(":")
        if separator and is_game_option(key, value, rules):
            result[key] = value
        elif strict:
            raise ValueError(f"The modpack contains an unsupported game setting: {key}")
    return result


def pack_game_options(text, content, rules):
    options = read_game_options(text, rules)
    supported = {"vanilla", "mod_resources", "mod/punchy:punchy", "Moonlight Mods Dynamic Assets"}
    supported.update("file/" + path.removeprefix("resourcepacks/") for path in content if path.startswith("resourcepacks/"))
    for key in ("resourcePacks", "incompatibleResourcePacks"):
        if key in options:
            options[key] = json.dumps([name for name in json.loads(options[key]) if name in supported], ensure_ascii=False)
    options.update(rules["GameOptions"]["Defaults"])
    return "".join(f"{key}:{value}\n" for key, value in options.items())
