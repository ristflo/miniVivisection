"""Publishes a release to the public repository (ristflo/miniVivisection) as one commit: the files
tools/public_files.txt names, as a tag of this (private) repository holds them -- never the working
tree, never this repository's history.

    python tools/publish_public.py --check [ref]   # only check what would go out (default ref: HEAD)
    python tools/publish_public.py v0.2.0-beta.365 # commit it in the public clone, tag it, ask, push
    python tools/publish_public.py --update "the pages link the source"   # HEAD, for the last release: no new tag

The public clone is C:\\Dev\\miniVivisection-public (--clone to name another), a clone of
https://github.com/ristflo/miniVivisection: its files are replaced by the release's (files the
release no longer has are deleted), then one commit "miniVivisection <version>" by the GitHub
noreply address, tagged with the release's tag. Pushing asks first (--yes doesn't); --no-push
leaves the commit for a look (git -C <clone> show --stat).

Before anything is written, every file is checked: no personal e-mail address (the noreply one is
fine), no password, key or token, no file over 20 MB, and in text files no path into a user's
folder (C:\\Users\\<name>) or the account's name; a file the check names stops the publication.
In binary files (a .3dm Rhino saved with its own cache paths, a package) those two are only listed. The private repository is tagged
public/<version> with the commit it came from.
"""
import fnmatch
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLONE = r'C:\Dev\miniVivisection-public'
LIST = os.path.join(HERE, 'tools', 'public_files.txt')
NAME = 'Florian Rist'
EMAIL = '27972398+ristflo@users.noreply.github.com'
MAX_SIZE = 20 * 1024 * 1024

# Never public: personal addresses and the account's user name, folders in a user's profile, secrets.
# The names are put together here, so this file doesn't name them itself.
USER = 'fri' + 'st'
DOMAINS = ['tuwien' + r'\.ac\.at', 'fs' + r'\.tum\.de']
FORBIDDEN = [
    (re.compile(('%s@|florian%srist@|%s' % (USER, r'\.', '|'.join(DOMAINS))).encode(), re.I), 'a personal e-mail address'),
    (re.compile(rb'"(?:web)?[sS]ftpPassword"\s*:'), 'an SFTP password'),
    (re.compile(rb'-----BEGIN (?:RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----'), 'a private key'),
    (re.compile(rb'\b(?:ghp|gho|ghu|ghs)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}'), 'a GitHub token'),
    (re.compile(rb'\bsk-ant-[A-Za-z0-9_-]{20,}'), 'an Anthropic key'),
    (re.compile(rb'\bAKIA[0-9A-Z]{16}\b'), 'an AWS key'),
]
# The user's folder and name: a problem in a text file; in a binary one (a model Rhino saved with its
# own cache paths, a package of it) only reported -- Rhino writes them on every save.
PERSONAL = [
    (re.compile(rb'[A-Za-z]:(?:\\\\|\\|/)Users(?:\\\\|\\|/)(?!Public\b|me\b)[A-Za-z]', re.I), "a path into a user's folder"),  # "me": the tests' made-up user
    (re.compile((r'\b%s\b' % USER).encode(), re.I), "the account's user name"),
]
EMAILS = re.compile(rb'[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}')
EMAILS_OK = (b'noreply@anthropic.com', b'users.noreply.github.com', b'me@x.org')  # Claude's co-author line, GitHub's noreply, a test's text


def git(*args, cwd=HERE, binary=False):
    out = subprocess.run(['git'] + list(args), cwd=cwd, capture_output=True, check=True)
    return out.stdout if binary else out.stdout.decode('utf-8')


def patterns():
    rules = []
    for line in open(LIST, encoding='utf-8'):
        line = line.strip()
        if line and not line.startswith('#'):
            sign, pattern = line.split(None, 1)
            rules.append((sign == '+', pattern))
    return rules


def public(path, rules):
    take = False
    for include, pattern in rules:
        if fnmatch.fnmatchcase(path, pattern):
            take = include
    return take


def files(ref):
    """{path: bytes} of what goes public from ref."""
    rules = patterns()
    paths = [p for p in git('ls-tree', '-r', '--name-only', ref).splitlines() if public(p, rules)]
    return {p: git('show', '%s:%s' % (ref, p), binary=True) for p in paths}


def problems(contents):
    """([(path, what)], [(path, what)]) -- what keeps a file from going public, and what is only said."""
    found, warnings = [], []
    for path, data in contents.items():
        text = b'\0' not in data[:8000]
        if len(data) > MAX_SIZE:
            found.append((path, 'over 20 MB'))
        for pattern, what in FORBIDDEN:
            if pattern.search(data) or (not text and pattern.search(data.replace(b'\0', b''))):  # UTF-16 too
                found.append((path, what))
        for pattern, what in PERSONAL:
            if pattern.search(data) or (not text and pattern.search(data.replace(b'\0', b''))):
                (found if text else warnings).append((path, what))
        if text:  # every e-mail address must be one of the known ones
            for address in set(EMAILS.findall(data)):
                if not any(ok in address for ok in EMAILS_OK) and not address.startswith(b'n@rhinoviv'):
                    found.append((path, 'an e-mail address: %s' % address.decode('utf-8', 'replace')))
    return found, warnings


def check(ref):
    contents = files(ref)
    found, warnings = problems(contents)
    size = sum(len(d) for d in contents.values())
    print('%s: %d files, %.1f MB public' % (ref, len(contents), size / 1048576))
    for path, what in found:
        print('  ! %s: %s' % (path, what))
    for path, what in warnings:
        print('  (binary) %s: %s' % (path, what))
    return contents, found


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    clone = CLONE
    note = None
    for option in ('--clone', '--update'):
        if option in sys.argv:
            value = sys.argv[sys.argv.index(option) + 1]
            args = [a for a in args if a != value]
            if option == '--clone':
                clone = value
            else:
                note = value
    ref = args[0] if args else 'HEAD'
    contents, found = check(ref)
    if found:
        raise SystemExit('Not published: %d problem(s) above.' % len(found))
    if '--check' in sys.argv:
        return
    if note is not None:
        # An update of the last release (its pages, what is public): no new tag -- a published tag doesn't move
        release = git('describe', '--tags', '--abbrev=0', '--match', 'v*', ref).strip()
        version = release[1:]
    elif ref.startswith('v'):
        version = ref[1:]
    else:
        raise SystemExit('Publish a release tag (v0.2.0-beta.N), or --update "what changed" for the last release; not %s.' % ref)
    if not os.path.isdir(os.path.join(clone, '.git')):
        raise SystemExit('No public clone at %s: git clone https://github.com/ristflo/miniVivisection.git "%s"' % (clone, clone))
    # The clone's files become the release's: the old ones out, the release's in
    for path in git('ls-files', cwd=clone).splitlines():
        os.remove(os.path.join(clone, path))
    for path, data in contents.items():
        target = os.path.join(clone, *path.split('/'))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, 'wb') as f:
            f.write(data)
    git('add', '-A', cwd=clone)
    if not git('status', '--porcelain', cwd=clone).strip():
        print('The public repository has this release already.')
        return
    env = dict(os.environ, GIT_AUTHOR_NAME=NAME, GIT_AUTHOR_EMAIL=EMAIL, GIT_COMMITTER_NAME=NAME, GIT_COMMITTER_EMAIL=EMAIL)
    commit = git('rev-parse', ref + '^{commit}').strip()
    if note is None:
        message = 'miniVivisection %s\n\nThe release %s: %s\n' % (version, version, 'https://restweiss.de/miniVivisection')
        subprocess.run(['git', 'commit', '-q', '-m', message], cwd=clone, env=env, check=True)
        subprocess.run(['git', 'tag', '-a', ref, '-m', 'miniVivisection ' + version], cwd=clone, env=env, check=True)
        subprocess.run(['git', 'tag', '-f', 'public/' + version, commit], cwd=HERE, check=True, capture_output=True)
        print('Committed in %s: miniVivisection %s (from %s, tagged public/%s here).' % (clone, version, commit[:7], version))
    else:
        message = 'miniVivisection %s, updated: %s\n' % (version, note)
        subprocess.run(['git', 'commit', '-q', '-m', message], cwd=clone, env=env, check=True)
        subprocess.run(['git', 'tag', '-f', 'public/%s-update' % version, commit], cwd=HERE, check=True, capture_output=True)
        print('Committed in %s: miniVivisection %s, updated (from %s, tagged public/%s-update here).' % (clone, version, commit[:7], version))
    if '--no-push' in sys.argv:
        print('Not pushed. Look: git -C "%s" show --stat; push: git -C "%s" push --follow-tags' % (clone, clone))
        return
    if '--yes' not in sys.argv:
        if not sys.stdin.isatty():
            print('Not pushed (no console to ask in): git -C "%s" push --follow-tags' % clone)
            return
        if input('Push miniVivisection %s to the public repository? [y/N] ' % version).strip().lower() not in ('y', 'yes', 'j', 'ja'):
            print('Not pushed: git -C "%s" push --follow-tags' % clone)
            return
    subprocess.run(['git', 'push', '--follow-tags', 'origin', 'HEAD'], cwd=clone, check=True)
    print('Pushed.')


if __name__ == '__main__':
    main()
