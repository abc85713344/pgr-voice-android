"""只在主机生成纯像素回归夹具；原始手机画面与此夹具均不进入 APK。"""
import hashlib
import json
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parents[2] / 'reports' / 'xfold5-20260928' / '02-game-inner.png'
TARGET = HERE / 'fixtures'
TARGET.mkdir(exist_ok=True)
image = Image.open(SOURCE).convert('RGBA')
items = []
for width, height in [(320, 192), (160, 96)]:
    sample = image.resize((width, height), Image.Resampling.BILINEAR)
    # little-endian 0xAARRGGBB；C# 用 BitConverter.ToInt32 读取。
    output = TARGET / f'chapter29-narration-{width}x{height}.argb'
    output.write_bytes(sample.tobytes('raw', 'BGRA'))
    items.append({'file': output.name, 'width': width, 'height': height,
                  'sha256': hashlib.sha256(output.read_bytes()).hexdigest()})
(TARGET / 'provenance.json').write_text(json.dumps({
    'source': 'reports/xfold5-20260928/02-game-inner.png',
    'sourceSize': image.size,
    'sourceSha256': hashlib.sha256(SOURCE.read_bytes()).hexdigest(),
    'resample': 'Pillow BILINEAR',
    'scope': '用户当前第29章第一小节黑底居中旁白。只验证选区，不验证OCR和路线。',
    'fixtures': items,
}, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(items, ensure_ascii=False))
