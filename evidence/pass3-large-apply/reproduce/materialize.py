"""Materialize one unshipped experiment in an empty directory."""
import argparse
import subprocess
from pathlib import Path

BASE = '6c4c7f4b5c238698d286bff31026ff11ec760b66'
PARENTS = {
    'inverse': 'base', 'asint': 'inverse', 'cache': 'base',
    'tree': 'base', 'packed': 'tree', 'select': 'packed',
    'cal0': 'base', 'cal3': 'cal0', 'cal6': 'cal0', 'cal9': 'cal0',
    'pair': 'base', 'pairkey': 'pair', 'pairprobe': 'pair',
    'cursor': 'base', 'carry': 'base',
}

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('variant', choices=['base', *PARENTS])
parser.add_argument('destination', type=Path)
args = parser.parse_args()
output = args.destination.resolve()
output.mkdir(parents=True, exist_ok=True)
if any(output.iterdir()):
    parser.error('Select an empty output directory.')
repo = Path(__file__).resolve().parents[3]
patches = Path(__file__).resolve().parents[1] / 'patches'
(output / 'src').mkdir()
sources = subprocess.check_output(['git', 'ls-tree', '--name-only', BASE + ':src'], cwd=repo, text=True)
for name in sources.splitlines():
    content = subprocess.check_output(['git', 'show', f'{BASE}:src/{name}'], cwd=repo)
    (output / 'src' / name).write_bytes(content)
chain = []
variant = args.variant
while variant != 'base':
    parent = PARENTS[variant]
    chain.append(patches / f'{parent}-to-{variant}.patch')
    variant = parent
for patch in reversed(chain):
    subprocess.run(['patch', '--batch', '--forward', '-p1', '-i', str(patch)], cwd=output, check=True)
