import argparse
import hashlib
import io
import json
import re
import shutil
import tempfile
import tomllib
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen
from uuid import uuid4
from zipfile import ZIP_DEFLATED, ZipFile

from pack_worlds import clean_world_archives
from pack_settings import pack_game_options


ROOT = Path(__file__).parent
POLICY_PATH = ROOT / "src/ArysLauncher/pack-content-policy.json"
PACK_DIRS = (
    "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks",
    "datapacks", "resources", "resource", "kubejs", "scripts", "patchouli_books",
    "openloader", "global_packs", "paxi", "fancymenu", "moonlight-global-datapacks",
)
DOWNLOAD_HOSTS = {
    "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com",
    "mediafilez.forgecdn.net", "edge.forgecdn.net", "media.forgecdn.net",
}
MOD_LABEL = re.compile(r"^(?:\[[^\]\r\n]*[\u3400-\u9fff][^\]\r\n]*\]\s*)+")
PRIVATE_TEXT = re.compile(
    r"(?im)(?<![\w:])(?:[a-z]:[\\/]+(?=[\w.])|\\\\(?:users|home)\\|/(?:home|Users)/[\w.-]+)"
    r"|(?:ghp|gho|ghu|ghs)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,}"
    r"|-----BEGIN (?:EC |RSA |OPENSSH )?PRIVATE KEY-----"
    r"|^\s*[\x22']?(?:access[_-]?token|refresh[_-]?token|protectedtokens|password|api[_-]?key|"
    r"target_server_ip|target_server_name|server_address)[\x22']?\s*[:=]\s*[\x22']?[^\s\x22'#},]+"
)
TEXT_SUFFIXES = {".cfg", ".json", ".json5", ".toml", ".txt", ".properties", ".js", ".zs", ".mcmeta", ".yml", ".yaml"}
BETTER_COMBAT_PATH = "mods/bettercombat-forge-1.8.6+1.20.1.jar"
BETTER_COMBAT_PATCH = "0ec6c3653432c8083a5f98e4d807209004345132"
BETTER_COMBAT_UPSTREAM = "https://cdn.modrinth.com/data/5sy6g3kz/versions/UjlaL0Fm/bettercombat-forge-1.8.6%2B1.20.1.jar"
BETTER_COMBAT_SHA256 = "e147e13732e0fac83aee209487fdeb10a03e52c33e1b1439dbce01a80f3e8dbb"
BETTER_COMBAT_PRESET = "data/bettercombat/weapon_attributes/sword.json"
COMBAT_DATAPACK = "moonlight-global-datapacks/Arys Combat Defaults.zip"
TRANSLUCENT_GUI_PATH = "resourcepacks/Translucent GUI.zip"
TRANSLUCENT_GUI_HASH = "1cc588f7ec196fc46d9ba167b42568cf005e7bf2"
USER_AGENT = "Aeracy/Arys-Launcher (pack publisher)"


def check_source(path):
    if path.is_symlink() or path.is_junction():
        raise ValueError(f"Pack input contains a linked path: {path}")


def mod_ids(archive, nested=False):
    if "META-INF/mods.toml" in archive.namelist():
        descriptor = tomllib.loads(archive.read("META-INF/mods.toml").decode("utf-8-sig"))
        yield from (mod["modId"] for mod in descriptor.get("mods", []))
    if not nested:
        for name in archive.namelist():
            if name.startswith("META-INF/jarjar/") and name.endswith(".jar"):
                with ZipFile(io.BytesIO(archive.read(name))) as dependency:
                    yield from mod_ids(dependency, nested=True)


def active_mods(instance):
    result = {"minecraft", "forge"}
    mods = instance / "mods"
    check_source(mods)
    for file in mods.glob("*.jar"):
        check_source(file)
        with ZipFile(file) as archive:
            result.update(mod_ids(archive))
    return result


def config_owners(rules):
    return {f"config/{path}".casefold(): owner for owner, paths in rules["ConfigOwners"].items() for path in paths}


def file_hashes(path, algorithms=("sha1", "sha512", "sha256")):
    digests = {name: hashlib.new(name) for name in algorithms}
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            for digest in digests.values():
                digest.update(chunk)
    return {name: digest.hexdigest() for name, digest in digests.items()}


def normalize_instance(instance, rules, installed):
    mods = instance / "mods"
    archive = instance / ".arysmod-backups"
    check_source(archive)
    backup = archive / ("pack-cleanup-" + uuid4().hex[:12])
    moves = []
    names = {file.name.casefold(): file for file in mods.glob("*.jar")}
    for source in sorted(names.values()):
        name = MOD_LABEL.sub("", source.name)
        if name == source.name:
            continue
        target = names.get(name.casefold())
        if target is None:
            target = mods / name
            names[name.casefold()] = source
        elif file_hashes(source, ("sha256",)) == file_hashes(target, ("sha256",)):
            target = backup / "mods" / source.name
        else:
            raise ValueError(f"Different JARs would use the same filename: {name}")
        moves.append((source, target))
    orphan_paths = {
        f"config/{path}" for owner, paths in rules["ConfigOwners"].items() if owner not in installed for path in paths
    }
    orphan_paths.update(rules["LegacyConfigPaths"])
    orphan_paths.update(rules["LegacyConfigPrefixes"])
    if "fragmentum" not in installed:
        orphan_paths.add("config/fragmentum")
    for relative in sorted(orphan_paths):
        source = instance / relative
        if source.exists():
            moves.append((source, backup / relative))
    for source, target in moves:
        for path in (source, *source.parents, target, *target.parents):
            check_source(path)
        if target.exists() or not source.is_relative_to(instance) or not target.is_relative_to(instance):
            raise ValueError(f"Cannot move {source.name} to {target.name}")
    finished = []
    try:
        for source, target in moves:
            target.parent.mkdir(parents=True, exist_ok=True)
            source.rename(target)
            finished.append((source, target))
    except OSError:
        for source, target in reversed(finished):
            target.rename(source)
        raise
    print(f"Local cleanup: {sum(target.parent == mods for _, target in moves)} renamed, "
          f"{sum(target.is_relative_to(backup) for _, target in moves)} backed up")
    if backup.exists():
        print(f"Backup: {backup}")


def collect_content(instance, rules, installed):
    excluded = {path.casefold() for path in rules["ExcludedPaths"] + rules["LegacyConfigPaths"]}
    prefixes = tuple(path.casefold() for path in rules["ExcludedPrefixes"] + rules["LegacyConfigPrefixes"])
    owners = config_owners(rules)
    content = {}
    names = set()
    for directory in PACK_DIRS:
        root = instance / directory
        check_source(root)
        if not root.is_dir():
            continue
        for folder, subdirs, files in root.walk():
            subdirs[:] = [
                name for name in subdirs if directory != "mods"
                and not ((folder / name).relative_to(instance).as_posix().casefold() + "/").startswith(prefixes)
            ]
            for name in subdirs + files:
                check_source(folder / name)
            for name in sorted(files, key=lambda value: (bool(MOD_LABEL.match(value)), value.casefold())):
                file = folder / name
                relative = file.relative_to(instance).as_posix()
                key = relative.casefold()
                if key in excluded or key.startswith(prefixes):
                    continue
                if directory == "config":
                    owner = owners.get(key)
                    if owner is None:
                        raise ValueError(f"Review this config and add its owner to the pack policy: {relative}")
                    if owner not in installed:
                        print(f"Skipped orphan config: {relative}")
                        continue
                if directory == "mods":
                    if file.suffix.lower() != ".jar":
                        continue
                    relative = "mods/" + MOD_LABEL.sub("", name)
                if directory in ("resourcepacks", "shaderpacks") and file.suffix.lower() != ".zip" and not (
                    directory == "shaderpacks" and name.lower().endswith(".zip.txt") and (folder / name[:-4]).is_file()
                ):
                    continue
                key = relative.casefold()
                if key in names:
                    original = next(path for path in content if path.casefold() == key)
                    if directory == "mods" and file_hashes(file, ("sha256",)) == file_hashes(content[original], ("sha256",)):
                        continue
                    raise ValueError(f"Conflicting pack paths: {relative}")
                names.add(key)
                content[relative] = file
    return content


def checked_urls(urls):
    for url in urls:
        parsed = urlsplit(url)
        if parsed.scheme != "https" or parsed.hostname not in DOWNLOAD_HOSTS or parsed.username is not None or parsed.fragment or parsed.query:
            raise ValueError("A download entry has an unsupported or private address.")
    if not urls:
        raise ValueError("A download entry has no source.")
    return urls


def download_record(relative, hashes, size, urls, env=None):
    record = {"path": relative, "hashes": {key: hashes[key] for key in ("sha1", "sha512")},
              "downloads": checked_urls(urls), "fileSize": size}
    if env is not None:
        record["env"] = env
    return record


def modrinth_record(relative, hashes, size):
    request = Request("https://api.modrinth.com/v2/version_file/" + hashes["sha1"], headers={"User-Agent": USER_AGENT})
    try:
        with urlopen(request, timeout=25) as response:
            release = json.load(response)
    except HTTPError as error:
        if error.code == 404:
            return None
        raise
    for file in release["files"]:
        if file["size"] == size and all(file["hashes"][key] == hashes[key] for key in ("sha1", "sha512")):
            return download_record(relative, hashes, size, [file["url"]])
    return None


def apply_combat_defaults(content, work):
    local = content.get(BETTER_COMBAT_PATH)
    if local is None or file_hashes(local, ("sha1",))["sha1"] != BETTER_COMBAT_PATCH:
        return
    if COMBAT_DATAPACK in content:
        raise ValueError("The source has both the known patched Better Combat JAR and an Arys combat datapack.")
    moonlight = content.get("config/moonlight-common.toml")
    if moonlight is None or tomllib.loads(moonlight.read_text(encoding="utf-8")).get("general", {}).get("global_datapacks_folder") != "moonlight-global-datapacks":
        raise ValueError("The patched Better Combat preset requires Moonlight's default global datapack directory.")
    official = work / "bettercombat.jar"
    request = Request(BETTER_COMBAT_UPSTREAM, headers={"User-Agent": USER_AGENT})
    with urlopen(request, timeout=30) as response, official.open("wb") as output:
        output.write(response.read(2 * 1024 * 1024))
    if file_hashes(official, ("sha256",))["sha256"] != BETTER_COMBAT_SHA256:
        raise ValueError("The Better Combat download differs from the reviewed upstream file.")
    datapack = work / "combat-defaults.zip"
    with ZipFile(local) as source, ZipFile(datapack, "w", compression=ZIP_DEFLATED) as archive:
        archive.writestr("pack.mcmeta", '{"pack":{"pack_format":15,"description":"Arys combat defaults"}}\n')
        archive.writestr(BETTER_COMBAT_PRESET, source.read(BETTER_COMBAT_PRESET))
    content[BETTER_COMBAT_PATH] = official
    content[COMBAT_DATAPACK] = datapack


def prepare_text(relative, source, work):
    data = source.read_text(encoding="utf-8-sig")
    sanitized = "".join(line for line in data.splitlines(keepends=True)
                        if not (line.lstrip().startswith(("#", "//")) and PRIVATE_TEXT.search(line)))
    if PRIVATE_TEXT.search(sanitized):
        raise ValueError(f"Review possible private settings in {relative} before publishing.")
    if sanitized == data:
        return source
    target = work / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(sanitized, encoding="utf-8")
    return target


def pack_options(instance, content, rules):
    file = instance / "options.txt"
    check_source(file)
    text = file.read_text(encoding="utf-8-sig") if file.is_file() else ""
    return pack_game_options(text, content, rules)


def prepare_pack(instance, template, destination, version, release_tag, repository, content_output, download_index, normalize_local):
    if destination.suffix.lower() != ".mrpack" or destination.exists():
        raise ValueError("Choose an unused .mrpack output path.")
    for path in (instance, *instance.parents):
        check_source(path)
    if not instance.is_dir():
        raise ValueError("The instance directory is missing.")
    rules = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    installed = active_mods(instance)
    if "arysdungeonmod" not in installed:
        raise ValueError("The instance is missing the ArysMod JAR.")
    if normalize_local:
        normalize_instance(instance, rules, installed)
    content = collect_content(instance, rules, installed)
    with ZipFile(template) as source:
        index = json.loads(source.read("modrinth.index.json"))
    if index["dependencies"] != {"minecraft": "1.20.1", "forge": "47.4.16"}:
        raise ValueError("Use a template matching the current Forge 1.20.1 pack.")
    reference = {record["hashes"]["sha1"]: record for record in index["files"]}
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=destination.parent) as work_directory:
        work = Path(work_directory)
        apply_combat_defaults(content, work)
        if TRANSLUCENT_GUI_PATH in content:
            digest = file_hashes(content[TRANSLUCENT_GUI_PATH], ("sha1",))["sha1"]
            if digest == TRANSLUCENT_GUI_HASH:
                content["resourcepacks/Translucent GUI.LICENSE.txt"] = ROOT / "docs/TranslucentGUI-LICENSE.txt"
        downloads = []
        update_sources = {}
        overrides = []
        if content_output is not None:
            content_output.mkdir(parents=True, exist_ok=False)
        for relative, source in sorted(content.items()):
            original_mod_hash = None
            if relative.startswith("mods/arysdungeonmod-"):
                clean = clean_world_archives(source, work / source.name, work)
                if clean != source:
                    original_mod_hash = file_hashes(source, ("sha256",))["sha256"]
                    source = clean
            if source.suffix.lower() in TEXT_SUFFIXES:
                source = prepare_text(relative, source, work)
            hashes = file_hashes(source)
            size = source.stat().st_size
            ref = reference.get(hashes["sha1"])
            record = None
            if relative.startswith("mods/arysdungeonmod-") and release_tag:
                url = f"https://github.com/{repository}/releases/download/{release_tag}/{Path(relative).name}"
                record = download_record(relative, hashes, size, [url])
            elif ref is not None and ref["fileSize"] == size and ref["hashes"]["sha512"] == hashes["sha512"]:
                record = download_record(relative, hashes, size, ref["downloads"], ref.get("env"))
            elif relative == BETTER_COMBAT_PATH and hashes["sha256"] == BETTER_COMBAT_SHA256:
                record = download_record(relative, hashes, size, [BETTER_COMBAT_UPSTREAM])
            elif relative.startswith(("mods/", "resourcepacks/", "shaderpacks/")) and source.suffix.lower() in (".jar", ".zip"):
                if relative != TRANSLUCENT_GUI_PATH or hashes["sha1"] != TRANSLUCENT_GUI_HASH:
                    record = modrinth_record(relative, hashes, size)
                    if record is None:
                        raise ValueError(f"Add a verified public download to the template for {relative}.")
            if record is not None:
                downloads.append(record)
                update_sources[relative] = {"Size": size, "Sha256": hashes["sha256"], "Downloads": record["downloads"]}
                if relative.startswith("mods/arysdungeonmod-") and release_tag:
                    update_sources[relative]["AssetName"] = Path(relative).name
                if original_mod_hash is not None:
                    update_sources[relative]["CleanedFromSha256"] = original_mod_hash
            else:
                overrides.append((relative, source))
            if content_output is not None:
                target = content_output / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
        index = {"formatVersion": 1, "game": "minecraft", "versionId": version,
                 "name": "The Maiden's Eternal Dungeon", "files": downloads, "dependencies": index["dependencies"]}
        temporary = work / destination.name
        with ZipFile(temporary, "x", compression=ZIP_DEFLATED, compresslevel=6) as archive:
            archive.writestr("modrinth.index.json", json.dumps(index, ensure_ascii=False, indent=2) + "\n")
            options = pack_options(instance, content, rules)
            if options:
                archive.writestr("overrides/options.txt", options)
                if content_output is not None:
                    (content_output / "options.txt").write_bytes(options.encode("utf-8"))
            for relative, source in overrides:
                archive.write(source, f"overrides/{relative}")
        temporary.rename(destination)
        if download_index is not None:
            download_index.write_text(json.dumps(update_sources, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{destination} | {destination.stat().st_size} bytes | {len(downloads)} downloads | {len(overrides)} overrides")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("instance", type=Path)
    parser.add_argument("template", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--release-tag")
    parser.add_argument("--repository", default="Aeracy/Arys-Launcher")
    parser.add_argument("--content-output", type=Path)
    parser.add_argument("--download-index", type=Path)
    parser.add_argument("--normalize-local", action="store_true")
    args = parser.parse_args()
    prepare_pack(args.instance.absolute(), args.template, args.output, args.version, args.release_tag, args.repository,
                 args.content_output, args.download_index, args.normalize_local)


if __name__ == "__main__":
    main()
