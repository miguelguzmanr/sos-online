# Compares two per-frame state logs aligned on their entry frames: spike-b-compare.py A.log L_A B.log L_B
import re,sys
def load(path,L):
    out={}
    for line in open(path,encoding='utf-8'):
        parts=line.rstrip('\n').split(' ',2)
        if len(parts)<3: continue
        f=int(parts[1].lstrip('f')); rest=parts[2]
        comps=dict(re.findall(r'(\w+)=\[([^\]]*)\]',rest))
        comps['loading']='loading' in rest.split(' lvl=')[0]
        out[f-L]=comps
    return out
a=load(sys.argv[1],int(sys.argv[2])); b=load(sys.argv[3],int(sys.argv[4]))
keys=['lvl','scenes','party','stats','gold','inv','combat','npcs','pos']
first={}
counts={k:0 for k in keys}
common=sorted(set(a)&set(b)); common=[k for k in common if k>=0]
def pos_close(x,y,tol=0.02):
    px=re.findall(r'(\S+)@\(([-\d.]+),([-\d.]+),([-\d.]+)\)',x or ''); py=re.findall(r'(\S+)@\(([-\d.]+),([-\d.]+),([-\d.]+)\)',y or '')
    if [p[0] for p in px]!=[p[0] for p in py]: return False
    return all(abs(float(u)-float(v))<=tol+1e-9 for p,q in zip(px,py) for u,v in zip(p[1:],q[1:]))
for k in common:
    for c in keys:
        if c=='pos' and pos_close(a[k].get(c),b[k].get(c)): continue
        if a[k].get(c)!=b[k].get(c):
            counts[c]+=1
            if c not in first: first[c]=k
print("frames compared (k>=0):",len(common),"range",common[0],common[-1])
print("(pos compared with the live check's 2 cm tolerance)")
for c in keys: print(f"{c:7} differs on {counts[c]:5} frames, first at L+{first.get(c)}")
for c in sorted(first,key=first.get)[:3]:
    k=first[c]; print(f"--- {c} at L+{k}\nA: {a[k][c][:400]}\nB: {b[k][c][:400]}")
