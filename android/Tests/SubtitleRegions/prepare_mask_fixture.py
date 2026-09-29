"""主机近似重放 AppSession.TextMask；Pillow 双线性不等价于 Android 的像素实现。"""
from pathlib import Path
import hashlib
import json
from PIL import Image

here = Path(__file__).resolve().parent
source_dir = here.parents[2] / 'reports' / 'xfold5-20260928'
target = here / 'fixtures'
items = []
for name in ('02-game-inner.png', '06-resume.png'):
    source = source_dir / name
    image = Image.open(source).convert('RGB')
    width, height = image.size
    x, y = int(.04 * width), int(.15 * height)
    w, h = int(.92 * width), int(.50 * height)
    sample = image.crop((x, y, x + w, y + h)).resize((384, 96), Image.Resampling.BILINEAR)
    mask = bytes(int(min(p) > 155 and max(p) - min(p) < 75) for p in sample.get_flattened_data())
    path = target / (source.stem + '.mask')
    path.write_bytes(mask)
    items.append({'source': f'reports/xfold5-20260928/{name}', 'sourceSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
                  'mask': path.name, 'maskSha256': hashlib.sha256(mask).hexdigest(), 'ink': sum(mask), 'crop': [x,y,w,h]})
(target / 'mask-provenance.json').write_text(json.dumps({
    'scope': '主机近似验证宽ROI缩小后有足够文字像素，不能代替Android实际Bitmap缩放测试。',
    'resample': 'Pillow BILINEAR', 'maskSize': [384,96], 'fixtures': items
}, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(items, ensure_ascii=False))
