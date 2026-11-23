// WSL side of the launcher. The Windows process keeps stdin open while it owns the service.
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawn } = require("node:child_process");

const [mode, port] = process.argv.slice(2);
const root = __dirname;
const home = os.homedir();
const runtime = path.join(root, "data", "runtime", process.env.WSL_DISTRO_NAME || "default");
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

function findExecutable(name) {
    for (const dir of env.PATH.split(":")) {
        if (!path.isAbsolute(dir)) continue;
        const file = path.join(dir, name);
        try { fs.accessSync(file, fs.constants.X_OK); return file; }
        catch { /* Continue searching PATH. */ }
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

async function main() {
    if (!["auto", "existing"].includes(mode) || !/^\d+$/.test(port) || +port < 1 || +port > 65535) return 1;
    const managed = path.join(runtime, "package", "bin", "cloudcli");
    let cli = findExecutable("cloudcli");
    if (!cli && fs.existsSync(managed)) cli = managed;
    if (!cli) {
        if (mode !== "auto") return 77;
        const npm = findExecutable("npm");
        if (!npm) return 80;
        const tmp = path.join(runtime, "tmp");
        fs.mkdirSync(tmp, { recursive: true });
        const stage = fs.mkdtempSync(path.join(tmp, "install-"));
        try {
            const code = await run(npm, ["install", "--global", "--prefix", stage, "@cloudcli-ai/cloudcli"],
                stage, { ...env, TMPDIR: tmp, npm_config_cache: path.join(runtime, "cache") });
            if (code || stopping) return 79;
            fs.accessSync(path.join(stage, "bin", "cloudcli"), fs.constants.X_OK);
            fs.renameSync(stage, path.join(runtime, "package"));
        } finally {
            fs.rmSync(stage, { recursive: true, force: true });
        }
        cli = managed;
    }
    const version = await run(cli, ["--version"]);
    if (version || stopping) return 1;
    return run(cli, ["start", "--port", port]);
}

main().then(code => { process.exitCode = code; }).catch(error => {
    fs.writeSync(log, String(error) + "\n");
    process.exitCode = 1;
}).finally(() => { process.stdin.destroy(); fs.closeSync(log); });
