"""仅供守卫验证的隐藏Tk窗；WM_CLOSE正常退出，不申请大内存。"""
import argparse,json,tkinter as tk
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--title',required=True);p.add_argument('--ready',required=True);a=p.parse_args()
assert a.title.startswith('NornGuardFixture_')
root=tk.Tk();root.withdraw();root.title(a.title)
root.protocol('WM_DELETE_WINDOW',root.destroy)
Path(a.ready).write_text(json.dumps(dict(WindowTitle=a.title,Ready=True)),encoding='utf-8')
root.mainloop()
