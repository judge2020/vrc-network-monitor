"""Exercise release gating without contacting GitHub or publishing anything."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

script = Path(__file__).with_name('release.sh').resolve()
with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    bin_dir = root / 'bin'
    bin_dir.mkdir()
    (root / 'vrc-network-monitor-win-x64.zip').write_bytes(b'test artifact')
    for name, body in {
        'git': '''import sys
print('Fix $(touch UNEXPECTED); `whoami`' if '--format=%s' in sys.argv else 'Subject\\n\\nBody with "quotes" and $HOME')
''',
        'gh': '''import json, os, pathlib, sys
if sys.argv[1] == 'api':
    assert sys.argv[2] == 'repos/judge2020/vrc-network-monitor/git/ref/heads/main'
    if os.environ['MOCK_HEAD'] == 'error': sys.exit(1)
    print(os.environ['MOCK_HEAD'])
else:
    assert sys.argv[1:3] == ['release', 'create']
    args = sys.argv[3:]
    notes = pathlib.Path(args[args.index('--notes-file') + 1]).read_text()
    pathlib.Path('published.json').write_text(json.dumps({'args': args, 'notes': notes}))
''',
    }.items():
        command = bin_dir / name
        command.write_text('#!/usr/bin/env python3\n' + body)
        command.chmod(0o755)
    env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ['PATH'],
               GITHUB_SHA='a' * 40, GITHUB_REF='refs/heads/main',
               GITHUB_REPOSITORY='judge2020/vrc-network-monitor')
    for head, exit_code, publishes in [('b' * 40, 0, False), ('error', 1, False), ('a' * 40, 0, True)]:
        result = subprocess.run(['bash', str(script)], cwd=root, env=dict(env, MOCK_HEAD=head), capture_output=True, text=True)
        assert result.returncode == exit_code, result.stderr
        output = root / 'published.json'
        assert output.exists() == publishes
        if publishes:
            data = json.loads(output.read_text())
            args = data['args']
            assert args[0] == 'build-' + env['GITHUB_SHA']
            assert args[args.index('--target') + 1] == env['GITHUB_SHA']
            assert args[args.index('--title') + 1] == 'Fix $(touch UNEXPECTED); `whoami`'
            assert data['notes'] == 'Subject\n\nBody with "quotes" and $HOME\n'
            assert not (root / 'UNEXPECTED').exists()
        print(f'PASS: {head[:8]} -> exit {exit_code}, publishes={publishes}')
