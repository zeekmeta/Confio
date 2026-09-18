#!/usr/bin/env python3
"""在隔离包源、缓存和密钥目录中复用真实消费者，无需桌面密钥服务。"""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import stat
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


TARGETS = ("net8.0", "net9.0", "net10.0")
REPOSITORY = Path(__file__).resolve().parent.parent


def isolated_environment(root):
    # 独占用户数据目录，并让桌面总线不可用，验证默认保护无需交互。
    environment = os.environ.copy()
    for name, folder in (("XDG_DATA_HOME", "data"), ("XDG_CONFIG_HOME", "config"),
                         ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "run")):
        path = root / "session" / folder
        path.mkdir(parents=True, exist_ok=True, mode=0o700)
        environment[name] = str(path)
    environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + str(root / "absent-bus")
    return environment


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path, help="待验证的 Confio .nupkg")
    parser.add_argument("--output", type=Path, default=REPOSITORY / "artifacts/package-verification-linux",
                        help="在此目录下创建本次独立产物目录；WSL 建议使用 Linux 文件系统")
    parser.add_argument("--native-aot", action="store_true", help="同时发布并运行三个现代目标的 Native AOT")
    parser.add_argument("--portable-input", type=Path, help="读取 Windows 验证产物中的 portable 目录")
    args = parser.parse_args()
    if platform.system() != "Linux":
        parser.error("This entry point requires Linux.")
    for tool in ("dotnet",):
        if shutil.which(tool) is None:
            parser.error("Required executable is missing from PATH: " + tool)
    rid = {"x86_64": "linux-x64", "aarch64": "linux-arm64"}.get(platform.machine())
    if rid is None:
        parser.error("Consumer verification supports linux-x64 and linux-arm64.")

    args.output.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="confio-", dir=args.output.resolve()))
    print("Verification artifacts: " + str(root), flush=True)
    for folder in ("feed", "logs", "packages"):
        (root / folder).mkdir()
    candidate = root / "feed" / args.package.name
    shutil.copyfile(args.package, candidate)
    with zipfile.ZipFile(candidate) as archive:
        manifest = ET.fromstring(archive.read("Confio.nuspec"))
        version = manifest.find("{*}metadata/{*}version").text
    print("Candidate SHA256: " + hashlib.sha256(candidate.read_bytes()).hexdigest(), flush=True)

    def run(label, command, environment=None, timeout=600):
        log = root / "logs" / (label + ".log")
        with log.open("w") as output:
            result = subprocess.run(list(map(str, command)), env=environment, stdout=output,
                                    stderr=subprocess.STDOUT, timeout=timeout)
        if result.returncode:
            raise RuntimeError(f"{label} failed ({result.returncode}); see {log}")
        print("PASS: " + label, flush=True)

    shutil.copyfile(REPOSITORY / "Directory.Build.props", root / "Directory.Build.props")
    versions = ET.parse(REPOSITORY / "Directory.Packages.props")
    ET.SubElement(versions.getroot().find("ItemGroup"), "PackageVersion", Include="Confio", Version=version)
    versions.write(root / "Directory.Packages.props", encoding="utf-8")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    mapping = ET.SubElement(config, "packageSourceMapping")
    for name, source, pattern in (("candidate", str(root / "feed"), "Confio"),
                                  ("nuget.org", "https://api.nuget.org/v3/index.json", "*")):
        ET.SubElement(sources, "add", key=name, value=source)
        ET.SubElement(ET.SubElement(mapping, "packageSource", key=name), "package", pattern=pattern)
    ET.SubElement(ET.SubElement(config, "config"), "add", key="globalPackagesFolder", value=str(root / "packages"))
    ET.ElementTree(config).write(root / "NuGet.Config", encoding="utf-8")
    for name in ("ConfioConsumerModels", "ConfioConsumer"):
        destination = root / name
        destination.mkdir()
        source = REPOSITORY / "tests" / name
        for code in source.glob("*.cs"):
            shutil.copyfile(code, destination / code.name)
        project = ET.parse(source / (name + ".csproj"))
        project.find("PropertyGroup/TargetFrameworks").text = (
            ";".join(TARGETS) if name == "ConfioConsumer" else "net8.0;net10.0")
        for group in project.findall("ItemGroup"):
            for reference in group.findall("ProjectReference"):
                group.remove(reference)
        group = ET.SubElement(project.getroot(), "ItemGroup")
        ET.SubElement(group, "PackageReference", Include="Confio")
        if name == "ConfioConsumer":
            ET.SubElement(group, "ProjectReference", Include="../ConfioConsumerModels/ConfioConsumerModels.csproj")
        project.write(destination / (name + ".csproj"), encoding="utf-8")

    consumer = root / "ConfioConsumer/ConfioConsumer.csproj"
    run("restore", ["dotnet", "restore", consumer, "--configfile", root / "NuGet.Config"])
    run("build", ["dotnet", "build", consumer, "-c", "Release", "--no-restore"])
    for name in ("ConfioConsumer", "ConfioConsumerModels"):
        assets = json.loads((root / name / "obj/project.assets.json").read_text())
        for target, libraries in assets["targets"].items():
            asset = "net10.0" if target == "net10.0" else "net8.0"
            library = libraries["Confio/" + version]
            for kind in ("compile", "runtime"):
                if "lib/" + asset + "/Confio.dll" not in library[kind]:
                    raise RuntimeError("Incorrect Confio asset: " + target)
    restored = root / "packages/confio" / version
    metadata = json.loads((restored / ".nupkg.metadata").read_text())
    if Path(metadata["source"]).resolve() != root / "feed":
        raise RuntimeError("Confio was not restored from the candidate feed.")

    variants = []
    for target in TARGETS:
        output = root / "ConfioConsumer/bin/Release" / target
        if any(path.name.startswith(("ConfioGenerator", "Microsoft.CodeAnalysis", "PolySharp"))
               for path in output.glob("*.dll")):
            raise RuntimeError("Compiler dependencies entered the runtime output.")
        # JIT 使用独立发布的真实目标运行时，不要求机器预装三个系统运行时。
        managed = root / "managed" / target
        run("publish-jit-" + target, ["dotnet", "publish", consumer, "-c", "Release", "-f", target,
                                     "-r", rid, "--self-contained", "true", "-p:PublishAot=false", "-o", managed])
        variants.append((target + "-jit", [managed / "ConfioConsumer"]))
        if args.native_aot:
            native = root / "native" / target
            run("publish-" + target, ["dotnet", "publish", consumer, "-c", "Release", "-f", target,
                                     "-r", rid, "-o", native])
            variants.append((target + "-aot", [native / "ConfioConsumer"]))

    environment = isolated_environment(root)
    for name, command in variants:
        run("aes-" + name, command + ["encryption"], environment)
    for name, command in (variants[0], variants[-1]):
        for extension in ("json", "yaml"):
            run("portable-write-" + name + "-" + extension,
                command + ["encryption", "write", root / "portable" / name / ("settings." + extension)], environment)
    if args.portable_input:
        shutil.copytree(args.portable_input, root / "portable/windows")
    fixtures = sorted(path for extension in ("json", "yaml")
                      for path in (root / "portable").rglob("settings." + extension))
    before = [path.read_bytes() for path in fixtures]
    for name, command in variants:
        for index, path in enumerate(fixtures):
            run(f"portable-read-{name}-{index}", command + ["encryption", "read", path], environment)
    if before != [path.read_bytes() for path in fixtures]:
        raise RuntimeError("Reading portable ciphertext changed its file.")

    for name, command in variants:
        run("consumer-" + name, command + ["--directory", root / "files"], environment)
    protected = root / "automatic-protection"
    key = protected / "keys/shared.key"
    with ThreadPoolExecutor(max_workers=2) as executor:
        writes = [executor.submit(run, "key-write-" + api,
                   command + ["file-key", "write", protected / (api + ".json"), key, api], environment)
                  for api, (_, command) in zip(("sync", "async"), (variants[0], variants[-1]))]
        for write in writes:
            write.result()
    stored_key = key.read_bytes()
    if len(stored_key) != 32 or stat.S_IMODE(key.stat().st_mode) != 0o600 or stat.S_IMODE(key.parent.stat().st_mode) != 0o700:
        raise RuntimeError("The automatic key requires 32 bytes, file mode 0600 and directory mode 0700.")
    for name, command in variants:
        for api in ("sync", "async"):
            run("key-read-" + name + "-" + api,
                command + ["file-key", "read", protected / (api + ".json"), key, api], environment)
    if key.read_bytes() != stored_key:
        raise RuntimeError("Restarted consumers changed the persisted key.")
    key.write_bytes(b"invalid-test-key")
    for action in ("corrupt", "missing"):
        if action == "missing":
            key.unlink()
        for name, command in variants:
            for api in ("sync", "async"):
                run("key-" + action + "-" + name + "-" + api,
                    command + ["file-key", action, protected / (api + ".json"), key, api], environment)
    for name, command in variants:
        run("key-cancel-" + name, command + ["file-key", "cancel",
            protected / ("cancel-" + name + ".json"), key, "async"], environment)
    print(f"PASS: isolated Linux Confio {version} verification. Artifacts: {root}", flush=True)


if __name__ == "__main__":
    main()
