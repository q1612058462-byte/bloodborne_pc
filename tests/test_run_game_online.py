import json
import hashlib
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import urllib.error
from contextlib import redirect_stdout, redirect_stderr

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import run_game


class OnlineOverridesTests(unittest.TestCase):
    def temporary_directory(self):
        return tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent)

    def test_routes_game_bootstrap_and_summons(self):
        with self.temporary_directory() as directory:
            environment = {
                'BB_COMMUNITY_SERVER': 'https://thehuntersdream.com',
                'BB_SHADNET_WEBAPI': 'http://192.168.1.20:31315',
            }
            response = urllib.error.HTTPError(
                'http://192.168.1.20:31315/summon_messenger/create', 400,
                'expected empty request rejection', {}, None)
            with mock.patch.object(run_game.urllib.request, 'urlopen', side_effect=response):
                run_game.online_overrides(environment, Path(directory))

            path = Path(environment['SHADPS4_HTTP_HOST_OVERRIDES_JSON'])
            routes = json.loads(path.read_text(encoding='utf-8'))
            self.assertEqual(routes[run_game.GAME_SERVER], run_game.COMMUNITY_SERVER)
            for scheme in ('http', 'https'):
                self.assertEqual(
                    routes[f'{scheme}://thehuntersdream.com:18671/summon_messenger'],
                    'http://192.168.1.20:31315')

    def test_custom_file_is_preserved(self):
        with self.temporary_directory() as directory:
            custom = Path(directory) / 'host_overrides.custom.json'
            custom.write_text('{"example.invalid":"http://127.0.0.1"}\n', encoding='utf-8')
            environment = {'BB_SHADNET_WEBAPI': 'http://127.0.0.1:31315'}
            with mock.patch.object(run_game.urllib.request, 'urlopen') as urlopen:
                run_game.online_overrides(environment, Path(directory))
            self.assertEqual(Path(environment['SHADPS4_HTTP_HOST_OVERRIDES_JSON']), custom)
            self.assertEqual(custom.read_text(encoding='utf-8'),
                             '{"example.invalid":"http://127.0.0.1"}\n')
            urlopen.assert_not_called()

    def test_lab_server_with_explicit_port_has_valid_summon_routes(self):
        with self.temporary_directory() as directory:
            environment = {
                'BB_COMMUNITY_SERVER': 'http://192.168.31.226:32443',
                'BB_SHADNET_WEBAPI': 'http://192.168.31.226:32315',
            }
            with mock.patch.object(run_game, 'summon_service', return_value=True):
                run_game.online_overrides(environment, Path(directory))
            routes = json.loads(Path(environment['SHADPS4_HTTP_HOST_OVERRIDES_JSON'])
                                .read_text(encoding='utf-8'))
            self.assertEqual(routes[run_game.GAME_SERVER], environment['BB_COMMUNITY_SERVER'])
            self.assertEqual(routes['http://192.168.31.226:32443/summon_messenger'],
                             environment['BB_SHADNET_WEBAPI'])
            self.assertNotIn('192.168.31.226:32443:18671', json.dumps(routes))

    def test_bbsc_wrapper_is_explicit_and_isolated(self):
        with self.temporary_directory() as directory:
            root = Path(directory)
            lab = root / 'bbsc'
            lab.mkdir()
            for name in run_game.BBSC_LAB_HASHES:
                (lab / name).touch()
            command = [Path('C:/game/bb-probe.exe'), 'boot-linked.bin', '--timeout', '0']
            values = {'BBSC_LAB_MOD': '1', 'BB_SEAMLESS_LAB': '1', 'BB_ONLINE': '1',
                      'BB_SHADNET_SERVER': '192.168.31.226:32313',
                      'BB_SHADNET_WEBAPI': 'http://192.168.31.226:32315',
                      'BB_COMMUNITY_SERVER': 'http://192.168.31.226:32443',
                      'BBSC_RELAY': '192.168.31.226:32444'}
            empty_hash = hashlib.sha256(b'').hexdigest()
            fake_hashes = {name: empty_hash for name in run_game.BBSC_LAB_HASHES}
            with mock.patch.object(run_game, 'WINDOWS', True), mock.patch.dict(os.environ, values), \
                    mock.patch.dict(run_game.BBSC_LAB_HASHES, fake_hashes):
                result = run_game.bbsc_lab_command(command, command[0], root)
                self.assertEqual(result[:7], [lab / 'bbsc-launch.exe', '--emu', command[0],
                                              '--dll', lab / 'bbsc.dll', '--cwd', run_game.ROOT])
                self.assertEqual(result[7:], command[1:])
                self.assertEqual(os.environ['SHADPS4_BLOODBORNE_SEAMLESS_COOP'], '0')
                self.assertEqual(os.environ['SHADPS4_BBSC_SHARED_PROGRESS'], '0')
                self.assertEqual(os.environ['SHADPS4_BBSC_DISCORD'], '0')

    def test_bbsc_wrapper_rejects_modified_binary(self):
        with self.temporary_directory() as directory:
            lab = Path(directory) / 'bbsc'
            lab.mkdir()
            for name in run_game.BBSC_LAB_HASHES:
                (lab / name).write_bytes(b'changed')
            values = {'BBSC_LAB_MOD': '1', 'BB_SEAMLESS_LAB': '1', 'BB_ONLINE': '1',
                      'BB_SHADNET_SERVER': '127.0.0.1:32313',
                      'BB_SHADNET_WEBAPI': 'http://127.0.0.1:32315',
                      'BB_COMMUNITY_SERVER': 'http://127.0.0.1:32443',
                      'BBSC_RELAY': '127.0.0.1:32444'}
            with mock.patch.object(run_game, 'WINDOWS', True), mock.patch.dict(os.environ, values):
                with self.assertRaisesRegex(ValueError, 'does not match official'):
                    run_game.bbsc_lab_command(['probe'], 'probe', directory)

    def test_bbsc_mod_report_exposes_log_and_missing_ready_marker(self):
        with self.temporary_directory() as directory:
            logs = Path(directory) / 'logs'
            logs.mkdir()
            report = logs / 'bbsc-mod-test.log'
            report.write_text('I Mod: BBSC mod loaded\nE Mod: BBSC is off\n', encoding='utf-8')
            out, err = io.StringIO(), io.StringIO()
            with mock.patch.dict(os.environ, {'BBSC_LAB_COMPONENT_DIR': directory}), \
                    redirect_stdout(out), redirect_stderr(err):
                run_game.bbsc_lab_report(directory, report.stat().st_mtime)
            self.assertIn(str(report), out.getvalue())
            self.assertIn('not confirmed', err.getvalue())

    def test_bbsc_windows_launch_forwards_explicit_output_handles(self):
        child = mock.MagicMock()
        child.__enter__.return_value = child
        child.stdout = io.StringIO('game diagnostic\n')
        child.wait.return_value = 0
        output = io.StringIO()
        with mock.patch.object(run_game, 'WINDOWS', True), \
                mock.patch.dict(os.environ, {'BBSC_LAB_MOD': '1'}), \
                mock.patch.object(run_game.subprocess, 'Popen', return_value=child) as popen, \
                redirect_stdout(output):
            self.assertEqual(run_game.start(['bbsc-launch.exe'], 'bb-probe.exe', None), 0)
        self.assertIn('game diagnostic', output.getvalue())
        self.assertEqual(popen.call_args.kwargs['stdout'], run_game.subprocess.PIPE)
        self.assertEqual(popen.call_args.kwargs['stderr'], run_game.subprocess.STDOUT)

    def test_bbsc_wrapper_rejects_production_or_bypassed_game_check(self):
        with mock.patch.object(run_game, 'WINDOWS', True):
            with mock.patch.dict(os.environ, {'BBSC_LAB_MOD': '1', 'BB_ONLINE': '1',
                                              'BB_SEAMLESS_LAB': ''}):
                with self.assertRaises(ValueError):
                    run_game.bbsc_lab_command(['probe'], 'probe', 'runtime')
            with mock.patch.dict(os.environ, {'BBSC_LAB_MOD': '1', 'BB_ONLINE': '1',
                                              'BB_SEAMLESS_LAB': '1', 'BB_SKIP_GAME_CHECK': '1'}):
                with self.assertRaises(ValueError):
                    run_game.bbsc_lab_command(['probe'], 'probe', 'runtime')

    def test_bbsc_lab_rejects_custom_routes(self):
        with self.temporary_directory() as directory:
            (Path(directory) / 'host_overrides.custom.json').write_text('{}', encoding='utf-8')
            with self.assertRaises(ValueError):
                run_game.online_overrides({'BBSC_LAB_MOD': '1'}, Path(directory))


if __name__ == '__main__':
    unittest.main()
