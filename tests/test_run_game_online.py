import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import urllib.error

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


if __name__ == '__main__':
    unittest.main()
