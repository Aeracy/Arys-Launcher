import re
from copy import copy
from shutil import copyfileobj
from zipfile import ZipFile


WORLD_DIRECTORY = "assets/arysdungeonmod/builtin_worlds/"
PLAYER_DIRECTORIES = {"playerdata", "stats", "advancements", "saves", "backups", "logs"}
WORLD_RUNTIME_FILES = {"level.dat", "level.dat_old", "session.lock", "usercache.json", "usernamecache.json"}


def is_world_archive(name):
    return name.startswith(WORLD_DIRECTORY) and name.endswith(".zip")


def is_world_runtime_entry(name):
    parts = name.replace("\\", "/").casefold().strip("/").split("/")
    return parts[-1] in WORLD_RUNTIME_FILES or any(part in PLAYER_DIRECTORIES for part in parts)


def copy_entry(source, target, entry):
    with source.open(entry) as input_file, target.open(copy(entry), "w") as output_file:
        copyfileobj(input_file, output_file, length=1024 * 1024)


def clean_world_archives(source_path, target_path, work):
    with ZipFile(source_path) as source:
        world_archives = {}
        removed = 0
        for entry in source.infolist():
            if not is_world_archive(entry.filename):
                continue
            with source.open(entry) as archive_bytes, ZipFile(archive_bytes) as archive:
                unwanted = [file for file in archive.infolist() if is_world_runtime_entry(file.filename)]
                if not unwanted:
                    continue
                destination = work / f"world-{len(world_archives)}.zip"
                with ZipFile(destination, "x") as output:
                    for file in archive.infolist():
                        if not is_world_runtime_entry(file.filename):
                            copy_entry(archive, output, file)
                world_archives[entry.filename] = destination
                removed += sum(not file.is_dir() for file in unwanted)
        if not world_archives:
            return source_path
        if any(re.fullmatch(r"META-INF/[^/]+\.(SF|RSA|DSA|EC)", name, re.IGNORECASE) for name in source.namelist()):
            raise ValueError("Remove test player data from the world templates before signing the ArysMod JAR.")
        with ZipFile(target_path, "x") as target:
            for entry in source.infolist():
                world = world_archives.get(entry.filename)
                if world is None:
                    copy_entry(source, target, entry)
                else:
                    with world.open("rb") as input_file, target.open(copy(entry), "w") as output_file:
                        copyfileobj(input_file, output_file, length=1024 * 1024)
    print(f"ArysMod world templates: removed {removed} runtime files from {len(world_archives)} archives")
    return target_path
