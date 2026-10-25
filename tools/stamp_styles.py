"""Stamps miniVivisection's default Markdown styles (styles/markdown_*.css) with the checksum of
their text: the first line says "default ... -- checksum <16 hex digits>", the SHA-256 of the
rest (line breaks as \n, trailing blanks of the whole cut). A file whose text still matches its
checksum is an unchanged default -- MarkdownStyle.IsPristineDefault -- which a Rhino file's copy
of the style may be replaced by the plug-in's current default. Run after editing a default:

    python tools/stamp_styles.py
"""
import glob
import hashlib
import os
import re

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STAMP = re.compile(r'(checksum )[0-9a-f]{16}')


def checksum(body):
    return hashlib.sha256(body.replace('\r\n', '\n').rstrip().encode('utf-8')).hexdigest()[:16]


def main():
    for path in sorted(glob.glob(os.path.join(HERE, 'src', 'miniVivisection', 'styles', 'markdown_*.css'))):
        text = open(path, encoding='utf-8').read().replace('\r\n', '\n')
        first, _, body = text.partition('\n')
        stamped = STAMP.sub(lambda m: m.group(1) + checksum(body), first, count=1)
        if stamped == first and checksum(body) not in first:
            raise SystemExit('%s: no "checksum <hex>" in the first line' % path)
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(stamped + '\n' + body)
        print('%s: %s' % (os.path.basename(path), checksum(body)))


if __name__ == '__main__':
    main()
