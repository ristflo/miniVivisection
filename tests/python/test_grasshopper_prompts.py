"""The prompts window in Grasshopper (Plan_Grasshopper_Prompts.md), checked from the sources:
the grasshopper tools Claude is told about are the ones miniVivisection.GH has, the RhinoAI
tools it's told about are in rhino_tools.json, V Python's Grasshopper helpers are valid
Python, and the manual lists the feature."""
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # the repository
SRC = os.path.join(ROOT, 'src', 'miniVivisection')  # the plug-in's sources
GHA = os.path.join(ROOT, 'src', 'miniVivisection.GH')


def _read(*parts):
    with open(os.path.join(*parts), encoding='utf-8') as f:
        return f.read()


def _tools():
    return re.findall(r'Name = "(gh_[a-z_]+)"', _read(GHA, 'GrasshopperTools.cs'))


def test_every_tool_is_in_claudes_prompt():
    chat = _read(SRC, 'GrasshopperChat.cs')
    prompt = chat[chat.index('private const string Prompt ='):chat.index('private const string RhinoAiPrompt')]
    tools = _tools()
    assert len(tools) == 9
    for tool in tools:
        assert tool in prompt, tool + ' is not in the prompt'


def test_the_rhinoai_tools_named_are_in_the_policy():
    chat = _read(SRC, 'GrasshopperChat.cs')
    prompt = chat[chat.index('private const string RhinoAiPrompt ='):chat.index('private const string WorkPrompt')]
    policy = json.loads(_read(SRC, 'rhino_tools.json'))
    known = set(policy['readOnly'] + policy['modify'] + policy['destructive'])
    named = set(re.findall(r'\b(g1_[a-z_]+|get_viewport_image)\b', prompt))
    assert named, 'no RhinoAI tools named'
    assert named <= known, 'not in rhino_tools.json: %s' % sorted(named - known)


def test_changing_tools_are_marked():
    """The tools that change the definition say so (Changes = true): read only leaves them out."""
    source = _read(GHA, 'GrasshopperTools.cs')
    blocks = re.findall(r'new Tool\s*\{(.*?)\n            \},', source, re.S)
    changes = {re.search(r'Name = "(gh_[a-z_]+)"', b).group(1): 'Changes = true' in b for b in blocks}
    assert changes == {
        'gh_get_selection': False, 'gh_get_data': False, 'gh_get_script': False, 'gh_capture_canvas': False,
        'gh_set_value': True, 'gh_set_script': True, 'gh_edit_objects': True, 'gh_disconnect': True, 'gh_add_note': True,
    }


def test_python_helpers_compile():
    source = _read(SRC, 'PythonTerminal.cs')
    block = source[source.index('internal const string GrasshopperHelpers ='):]
    block = block[:block.index(';\n')]
    python = ''.join(json.loads('"' + part + '"') for part in re.findall(r'"((?:[^"\\]|\\.)*)"', block))
    code = compile(python, 'GrasshopperHelpers', 'exec')
    names = {}
    exec(code, names)
    for name in ('ghdoc', 'ghselected', 'ghcanvas', 'ghfind'):
        assert callable(names.get(name)), name


def test_manual_lists_the_feature():
    manual = _read(ROOT, 'docs', 'miniVivisection_Manual.md')
    glance = manual[manual.index('## Features at a glance'):manual.index('## 1. Getting started')]
    assert 'Prompts in Grasshopper' in glance
    assert 'GrasshopperPrompts' in manual  # the command option
    for tool_word in ('one undo step', 'Read only', 'ghdoc()'):
        assert tool_word in manual, tool_word
