"""Plain-Python tests for the Painter plugin's parts that don't need Painter running.

Run from the repository root:  python -m unittest discover Tools/SubstancePainter/tests
"""

import enum
import http.client
import json
import os
import socket
import sys
import tempfile
import threading
import types
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from xiv_instant_edit import bridge, client, manifest, settings  # noqa: E402

JOB_ID = "0123456789abcdef0123456789abcdef"


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def job_data(**overrides) -> dict:
    data = {
        "schema": "instant-edit.painter-job", "version": 1, "jobId": JOB_ID, "capability": "cap",
        "callbackPort": 42428, "pluginVersion": "1.2.4", "displayName": "Player - top", "mesh": "mesh/top.obj",
        "textureSets": [{
            "name": "mt_top", "width": 2048, "height": 1024, "removeChannels": ["Metallic"],
            "channels": [{"type": "User0", "format": "L8", "label": "diffuse.a"}],
            "seeds": [{"channel": "BaseColor", "file": "seeds/t0.rgb.tga", "colorSpace": "color"}],
        }],
        "targets": [{"key": "top_base", "textureSet": "mt_top"}, {"key": "--top_norm", "textureSet": "mt_top"}],
        "export": {"exportShaderParams": False, "exportPresets": [], "exportList": []},
    }
    data.update(overrides)
    return data


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.job_dir = os.path.join(self.temp.name, "painter", JOB_ID)
        os.makedirs(self.job_dir)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, data) -> str:
        path = os.path.join(self.job_dir, "job.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(data, handle)
        return path

    def test_valid_job_resolves_paths_inside_the_folder(self):
        job = manifest.load(self.write(job_data()))
        self.assertEqual(job.job_id, JOB_ID)
        self.assertEqual(job.mesh_path, os.path.join(self.job_dir, "mesh", "top.obj"))
        self.assertEqual(job.texture_sets[0].seeds[0].path, os.path.join(self.job_dir, "seeds", "t0.rgb.tga"))
        self.assertEqual(job.target_keys(), {"top_base", "--top_norm"})

    def test_paths_outside_the_job_folder_are_refused(self):
        for mesh in ("../other.obj", "C:/x.obj", os.path.join(self.job_dir, "mesh", "top.obj")):
            with self.assertRaises(manifest.ManifestError, msg=mesh):
                manifest.load(self.write(job_data(mesh=mesh)))
        bad_seed = job_data()
        bad_seed["textureSets"][0]["seeds"][0]["file"] = "../../evil.tga"
        with self.assertRaises(manifest.ManifestError):
            manifest.load(self.write(bad_seed))

    def test_malformed_jobs_are_refused(self):
        cases = [
            job_data(schema="other"),
            job_data(jobId="f" * 32),
            job_data(callbackPort=0),
            job_data(targets=[{"key": "a", "textureSet": "missing"}]),
            job_data(targets=[{"key": "a", "textureSet": "mt_top"}, {"key": "A", "textureSet": "mt_top"}]),
            job_data(export={"exportPath": "C:/", "exportPresets": [], "exportList": []}),
        ]
        sizes = job_data()
        sizes["textureSets"][0]["width"] = 1000
        twice = job_data()
        twice["textureSets"].append(dict(twice["textureSets"][0]))
        cases.extend([sizes, twice])
        for data in cases:
            with self.assertRaises(manifest.ManifestError):
                manifest.load(self.write(data))
        with self.assertRaises(manifest.ManifestError):
            manifest.load(os.path.join(self.job_dir, "other.json"))

    def test_export_config_gets_the_folder_and_results_map_to_targets(self):
        job = manifest.load(self.write(job_data()))
        config = manifest.export_config(job, "C:\\cache\\export\\1")
        self.assertEqual(config["exportPath"], "C:/cache/export/1")
        self.assertNotIn("exportPath", job.export)
        files = manifest.match_exported_files(job, {("mt_top", ""): ["C:/x/top_base.tga", "C:/x/--TOP_NORM.tga"]})
        self.assertEqual([key for key, _ in files], ["--top_norm", "top_base"])
        with self.assertRaises(manifest.ManifestError):
            manifest.match_exported_files(job, {("mt_top", ""): ["C:/x/stray.tga"]})
        with self.assertRaises(manifest.ManifestError):
            manifest.match_exported_files(job, {("other", ""): ["C:/x/top_base.tga"]})


class SettingsTests(unittest.TestCase):
    def test_config_values_and_fallbacks(self):
        with tempfile.TemporaryDirectory() as temp:
            path = os.path.join(temp, "config.json")
            self.assertEqual(settings.load(path).painter_port, settings.DEFAULT_PAINTER_PORT)
            with open(path, "w") as handle:
                json.dump({"painterPort": 5000, "pluginPort": "x", "version": "1.3.0"}, handle)
            loaded = settings.load(path)
            self.assertEqual((loaded.painter_port, loaded.plugin_port, loaded.version), (5000, settings.DEFAULT_PLUGIN_PORT, "1.3.0"))
            with open(path, "w") as handle:
                handle.write("{ broken")
            self.assertEqual(settings.load(path), settings.Settings())


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.bridge = bridge.Bridge()
        self.port = free_port()
        self.assertTrue(self.bridge.start(self.port))
        self.bridge.publish({"ok": True, "addonVersion": "1.2.4", "projectOpen": False, "jobId": "", "settingUp": False})

    def tearDown(self):
        self.bridge.stop()

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        data = None if body is None else json.dumps(body).encode()
        connection.request(method, path, body=data, headers={"Content-Type": "application/json", **(headers or {})})
        response = connection.getresponse()
        payload = json.loads(response.read() or b"{}")
        connection.close()
        return response.status, payload

    def open_body(self, **overrides):
        body = {"schema": bridge.OPEN_SCHEMA, "version": 1, "jobId": JOB_ID, "manifestPath": "C:/cache/painter/x/job.json"}
        body.update(overrides)
        return body

    def test_status_reports_the_snapshot(self):
        status, body = self.request("GET", "/status")
        self.assertEqual((status, body["addonVersion"]), (200, "1.2.4"))

    def test_browser_requests_are_refused(self):
        status, body = self.request("GET", "/status", headers={"Origin": "https://example.com"})
        self.assertEqual((status, body["code"]), (403, "request_forbidden"))

    def test_open_queues_a_valid_job(self):
        status, body = self.request("POST", "/open", self.open_body())
        self.assertEqual((status, body["queued"]), (202, True))
        self.assertEqual(self.bridge.jobs.get_nowait()["jobId"], JOB_ID)

    def test_open_refuses_invalid_or_busy_requests(self):
        self.assertEqual(self.request("POST", "/open", self.open_body(schema="x"))[0], 400)
        self.assertEqual(self.request("POST", "/open", self.open_body(manifestPath="C:/evil.exe"))[0], 400)
        self.bridge.publish({"ok": True, "projectOpen": True, "jobId": "f" * 32})
        self.assertEqual(self.request("POST", "/open", self.open_body())[1]["code"], "project_open")
        self.bridge.publish({"ok": True, "projectOpen": True, "jobId": JOB_ID})
        self.assertEqual(self.request("POST", "/open", self.open_body())[0], 202)
        self.bridge.publish({"ok": True, "settingUp": True})
        self.assertEqual(self.request("POST", "/open", self.open_body())[1]["code"], "busy")


class ClientTests(unittest.TestCase):
    def test_candidate_ports_skip_invalid_and_duplicates(self):
        self.assertEqual(client.candidate_ports(42428, 42428), [42428])
        self.assertEqual(client.candidate_ports(0, 42428), [42428])
        self.assertEqual(client.candidate_ports(5000, 42428), [5000, 42428])

    def test_calls_fall_through_to_a_listening_port_and_report_refusals(self):
        received = []

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                received.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                status = 404 if self.path == "/painter/attach" else 202
                data = json.dumps({"ok": status < 400, "error": "unknown project"} if status >= 400 else {"ok": True}).encode()
                self.send_response(status)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            dead = free_port()
            port, status, body = client.call([dead, server.server_address[1]], "/painter/send", {"a": 1})
            self.assertEqual((port, status, body["ok"], received[-1]), (server.server_address[1], 202, True, {"a": 1}))
            with self.assertRaises(client.PluginRefused) as refused:
                client.call([server.server_address[1]], "/painter/attach", {})
            self.assertIn("unknown project", str(refused.exception))
            with self.assertRaises(client.PluginUnavailable):
                client.call([dead], "/status", timeout=1)
        finally:
            server.shutdown()
            server.server_close()


def install_painter_stub():
    """A minimal stand-in for Painter's API, enough to import painter_job."""
    package = types.ModuleType("substance_painter")
    names = ["colormanagement", "export", "layerstack", "project", "resource", "textureset"]
    for name in names:
        module = types.ModuleType(f"substance_painter.{name}")
        setattr(package, name, module)
        sys.modules[f"substance_painter.{name}"] = module
    sys.modules["substance_painter"] = package

    class ExportStatus(enum.Enum):
        Success = 0
        Cancelled = 1
        Warning = 2
        Error = 3

    package.export.ExportStatus = ExportStatus
    package.export.calls = []

    def export_project_textures(config):
        package.export.calls.append(config)
        return types.SimpleNamespace(status=ExportStatus.Success, message="", textures={
            ("mt_top", ""): [config["exportPath"] + "/top_base.tga", config["exportPath"] + "/--top_norm.tga"],
        })

    package.export.export_project_textures = export_project_textures
    return package


class PainterJobTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.painter = install_painter_stub()
        from xiv_instant_edit import painter_job
        cls.painter_job = painter_job

    def state(self):
        targets = (manifest.TargetSpec("top_base", "mt_top", "Base"), manifest.TargetSpec("--top_norm", "mt_top", "Normal"))
        return self.painter_job.JobState(JOB_ID, "cap", 42428, "Player - top", "C:/cache/painter/" + JOB_ID,
                                         {"exportPresets": [], "exportList": []}, targets, {"mt_top": 17})

    def test_metadata_round_trips(self):
        state = self.state()
        copy = self.painter_job.JobState.from_metadata(json.loads(json.dumps(state.to_metadata())))
        self.assertEqual((copy.job_id, copy.targets, copy.base_layers), (state.job_id, state.targets, state.base_layers))
        with self.assertRaises(manifest.ManifestError):
            self.painter_job.JobState.from_metadata({"version": 2})

    def test_export_targets_the_folder_and_maps_every_target(self):
        with tempfile.TemporaryDirectory() as temp:
            files = self.painter_job.run_export(self.state(), temp)
        self.assertEqual([key for key, _ in files], ["--top_norm", "top_base"])
        self.assertEqual(self.painter.export.calls[-1]["exportPath"], temp.replace("\\", "/"))


if __name__ == "__main__":
    unittest.main()
