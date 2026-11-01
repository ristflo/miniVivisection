"""THIRD-PARTY-NOTICES.txt names every library that ships: each third-party .dll next to the built
.rhp (bin/Release, when built), and each library Lib.Harmony merged into 0Harmony.dll."""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(ROOT, 'src', 'miniVivisection', 'bin', 'Release', 'net7.0')
OURS = ('miniVivisection.rhp', 'miniVivisection.GH.gha', 'miniVivisection.dll', 'miniVivisection.GH.dll')
# a name in 0Harmony.dll's bytes -> the name the notices give it
MERGED = {b'MonoMod.Core': 'MonoMod', b'Mono.Cecil': 'Mono.Cecil', b'Microsoft.Cci': 'Microsoft.Cci.Pdb', b'Iced': 'Iced'}


def _notices():
    with open(os.path.join(ROOT, 'THIRD-PARTY-NOTICES.txt'), encoding='utf-8') as f:
        return f.read()


def test_every_shipped_library_has_a_notice():
    if not os.path.isdir(BUILD):
        return  # not built here
    notices = _notices()
    for name in os.listdir(BUILD):
        if name.endswith('.dll') and name not in OURS:
            assert os.path.splitext(name)[0] in notices, 'THIRD-PARTY-NOTICES.txt lacks %s' % name


def test_libraries_merged_into_harmony_have_a_notice():
    harmony = os.path.join(BUILD, '0Harmony.dll')
    if not os.path.isfile(harmony):
        return
    with open(harmony, 'rb') as f:
        data = f.read()
    notices = _notices()
    for marker, name in MERGED.items():
        if marker in data:
            assert name in notices, 'THIRD-PARTY-NOTICES.txt lacks %s (merged into 0Harmony.dll)' % name
