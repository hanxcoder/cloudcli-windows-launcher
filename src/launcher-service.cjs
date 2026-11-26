// WSL side of the launcher. The Windows process keeps stdin open while it owns the service.
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawn } = require("node:child_process");

const [mode, port, hintCli] = process.argv.slice(2);
const root = __dirname;
const home = os.homedir();
const runtime = path.join(home, ".local", "share", "cloudcli-launcher");
const legacyRuntime = path.join(root, "data", "runtime", process.env.WSL_DISTRO_NAME || "default");
const env = { ...process.env, PATH: path.dirname(process.execPath) + ":" + process.env.PATH };
fs.mkdirSync(path.join(root, "logs"), { recursive: true });
const log = fs.openSync(path.join(root, "logs", "server.log"), "w");
let child;
let stopping = false;

function stop() {
    stopping = true;
    if (child && child.pid) {
        try { process.kill(-child.pid, "SIGTERM"); }
        catch (error) { if (error.code !== "ESRCH") throw error; }
    }
}
process.stdin.on("end", stop);
process.stdin.on("error", stop);
process.on("SIGTERM", stop);
process.stdin.resume();

function isExecutable(file) {
    if (!file) return false;
    try { fs.accessSync(file, fs.constants.X_OK); return true; }
    catch { return false; }
}

function findExecutable(name) {
    for (const dir of env.PATH.split(":")) {
        if (!path.isAbsolute(dir)) continue;
        const file = path.join(dir, name);
        if (isExecutable(file)) return file;
    }
}

function findManagerCli() {
    const candidates = [
        path.join(home, ".volta", "bin", "cloudcli"),
        path.join(home, ".local", "share", "pnpm", "cloudcli"),
        path.join(home, ".asdf", "shims", "cloudcli"),
        path.join(home, ".local", "share", "mise", "shims", "cloudcli"),
        path.join(home, ".proto", "shims", "cloudcli"),
        path.join(home, ".proto", "bin", "cloudcli"),
        path.join(home, ".npm-global", "bin", "cloudcli"),
        path.join(home, ".nix-profile", "bin", "cloudcli"),
        "/home/linuxbrew/.linuxbrew/bin/cloudcli"
    ];
    for (const file of candidates) {
        if (isExecutable(file)) return file;
    }
}

async function run(file, args, cwd = home, environment = env) {
    if (stopping) return 1;
    return new Promise((resolve, reject) => {
        child = spawn(file, args, { cwd, env: environment, detached: true, stdio: ["ignore", log, log] });
        child.once("error", reject);
        child.once("close", code => { child = undefined; resolve(code ?? 1); });
    });
}

function report(stage) {
    try { process.stdout.write("PROGRESS:" + stage + "\n"); }
    catch { /* Ignore closed stdout. */ }
}

async function main() {
    if (!["auto", "existing"].includes(mode) || !/^\d+$/.test(port) || +port < 1 || +port > 65535) return 1;
    report("checking_cli");
    const managed = path.join(runtime, "package", "bin", "cloudcli");
    const legacyManaged = path.join(legacyRuntime, "package", "bin", "cloudcli");
    let cli = findExecutable("cloudcli");
    if (!cli && hintCli && path.isAbsolute(hintCli) && isExecutable(hintCli)) cli = hintCli;
    if (!cli) cli = findManagerCli();
    if (!cli && fs.existsSync(managed)) cli = managed;
    if (!cli && fs.existsSync(legacyManaged)) cli = legacyManaged;
    if (cli) {
        const cliDir = path.dirname(cli);
        if (!env.PATH.split(":").includes(cliDir)) env.PATH = env.PATH + ":" + cliDir;
    }
    if (!cli) {
        if (mode !== "auto") return 77;
        const npm = findExecutable("npm");
        if (!npm) return 80;
        report("installing_cli");
        const tmp = path.join(runtime, "tmp");
        fs.mkdirSync(tmp, { recursive: true });
        const stage = fs.mkdtempSync(path.join(tmp, "install-"));
        try {
            const code = await run(npm, ["install", "--global", "--no-audit", "--no-fund", "--prefix", stage, "@cloudcli-ai/cloudcli"],
                stage, { ...env, TMPDIR: tmp, npm_config_cache: path.join(runtime, "cache") });
            if (code || stopping) return 79;
            fs.accessSync(path.join(stage, "bin", "cloudcli"), fs.constants.X_OK);
            fs.rmSync(path.join(runtime, "package"), { recursive: true, force: true });
            fs.renameSync(stage, path.join(runtime, "package"));
        } finally {
            fs.rmSync(stage, { recursive: true, force: true });
        }
        cli = managed;
    }
    report("verifying_cli");
    const version = await run(cli, ["--version"]);
    if (version || stopping) return 1;
    report("starting_server");
    return run(cli, ["start", "--port", port]);
}

main().then(code => { process.exitCode = code; }).catch(error => {
    fs.writeSync(log, String(error) + "\n");
    process.exitCode = 1;
}).finally(() => { process.stdin.destroy(); fs.closeSync(log); });
