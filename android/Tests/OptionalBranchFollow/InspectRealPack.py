from pathlib import Path
import json
import sys
sys.stdout.reconfigure(encoding='utf-8')

for path in Path('<数据目录>/全部配音/01_主线').glob('第32*/pack.json'):
    p = json.loads(path.read_text(encoding='utf-8-sig'))
    print(path)
    nodes = {n['id']: n for n in p['nodes']}
    for n in p['nodes']:
        if n['id'] in ['ch32-71fcf055b68c2cfb1350-menu-003', 'ch32-782b137489a669098935-menu-011']:
            print(json.dumps({'id': n['id'], 'options': [{k:v for k,v in o.items() if k in ['label','targetId','mergeId','returnId','segmentIds']} for o in n['options']]}, ensure_ascii=False))
            for o in n['options']:
                current = o.get('targetId'); seen = set()
                while current and current not in seen:
                    seen.add(current); item = nodes[current]
                    print(json.dumps({k:v for k,v in item.items() if k in ['id','kind','text','nextId','pathId','completeRoute']}, ensure_ascii=False))
                    current = item.get('nextId')
                    if item['kind'] == 'merge': print('COMMON', json.dumps(nodes[current], ensure_ascii=False)); break
                    if len(seen) > 5 or item['kind'] in ('choice','gap'): break
