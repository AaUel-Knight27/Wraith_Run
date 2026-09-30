"""
Bot duel simulator (tuning aid - not used by the game).

A line-for-line Python port of BotAim.Step + the BotBrain fire gate, driven by the numbers parsed
straight out of scripts/bots/BotProfile.cs. After changing a value in BotProfile.cs, run

    python3 tools/bot_duel_sim.py

to see how a bot of each level fares against a standing / strafing target at 8, 18 and 32 metres
with the game's real AK-47 numbers (30 body / 90 head, 600 RPM, 0.6 deg spread, 3.5 deg kick).
  medTTK   median seconds to kill (lower = stronger)
  hit%     share of fired bullets that hit
  noKill%  duels not won within 8 seconds
"""
import os, re, math, random, statistics, sys
D2R = math.pi/180
HERE = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(HERE, '..', 'scripts', 'bots', 'BotProfile.cs')).read()

def parse(name):
    m = re.search(r'private static BotProfile Create%s\(\) => new BotProfile\s*\{(.*?)\n\t\};' % name, src, re.S)
    body = m.group(1)
    vals = {}
    for k, v in re.findall(r'(\w+)\s*=\s*([-\d\.]+)f?', body):
        vals[k] = float(v)
    for k, v in re.findall(r'(\w+)\s*=\s*(true|false)', body):
        vals[k] = (v == 'true')
    return vals
P = {n: parse(n) for n in ('Easy', 'Normal', 'Advanced')}

def wrap(a):
    while a > math.pi: a -= math.tau
    while a < -math.pi: a += math.tau
    return a

def sign(x): return (x>0)-(x<0)

class Aim:
    def __init__(s, rng): s.r=rng; s.vy=s.vp=0.0; s.ny=s.np=0.0; s.fy=s.fp=0.0; s.flicking=False; s.slow=False
    def axis(s, err, v, dt, maxs, acc, oc, tau):
        brake = acc*oc
        prof = sign(err)*min(maxs, math.sqrt(2*brake*abs(err)))
        prop = err/tau
        des = prop if abs(prop) < abs(prof) else prof
        step = des - v; lim = acc*dt; step = max(-lim, min(lim, step)); v += step
        return v, v*dt
    def step(s, dt, wy, wp, cy, cp, p, engaged, noise, ease_aim):
        ey = wrap(wy-cy); ep = wp-cp; mag = math.hypot(ey, ep); th = p['FlickThresholdDeg']*D2R
        sig = p['HandNoiseDeg']*D2R*noise; tau = max(p['HandNoiseTau'], 0.02); kick = sig*math.sqrt(2*dt/tau)
        s.ny += (-s.ny/tau)*dt + kick*s.r.gauss(0,1); s.np += (-s.np/tau)*dt + kick*s.r.gauss(0,1)
        if engaged:
            if (not s.flicking) and mag > th:
                do = s.r.random() < p['FlickUse']; s.slow = not do
                if do:
                    s.flicking = True; fe = p['FlickErrorDeg']*D2R*ease_aim
                    s.fy = s.r.gauss(0,1)*fe; s.fp = s.r.gauss(0,1)*fe*0.6
            if s.flicking and mag < 4*D2R: s.flicking = False
            if mag < 4*D2R: s.slow = False
            if not s.flicking:
                dec = math.exp(-dt/max(p['CorrectionTau']*ease_aim, 0.02)); s.fy *= dec; s.fp *= dec
            if s.flicking: ms, ac, oc = p['FlickSpeedDeg']*D2R, p['FlickAccelDeg']*D2R, p['Overconfidence']
            else:
                ms, ac, oc = p['TrackSpeedDeg']*D2R, p['FlickAccelDeg']*D2R*0.6, 1.0
                if s.slow: ms = min(ms, 170*D2R)
        else:
            ms, ac, oc = p['LookSpeedDeg']*D2R, p['LookSpeedDeg']*D2R*6, 1.0
        ty = cy + ey + s.ny + s.fy; tp = wp + s.np + s.fp; tt = max(p['TrackTau'], 0.02)
        s.vy, dy = s.axis(wrap(ty-cy), s.vy, dt, ms, ac, oc, tt)
        s.vp, dp = s.axis(tp-cp, s.vp, dt, ms, ac, oc, tt)
        return dy, dp

W = dict(body=30, head=90, rpm=600, rec_v=3.5, rec_h=2.0, rec_ms=180, spread=0.6)

def duel(level, dist, strafe, off, rng, ease=0.0, dt=1/60, max_t=8.0, weapon_full=True, move_pen=True):
    p = P[level]; aim = Aim(rng)
    t = 0; ty = 0.0; dirn = rng.choice([-1,1]); nxt = rng.uniform(0.35,1.0); spd = 4.7 if strafe else 0.0
    yaw = off*D2R*rng.choice([-1,1]); pitch = 0.0
    react = rng.uniform(p['ReactMin'], p['ReactMax'])*(1+ease)
    hist = []; posn = [0.0,0.0]; recy = recp = 0.0
    cool = 0.0; burst = 0; pause = 0.0
    dmg = 0.0; shots = hits = 0; first=None
    aimhead = rng.random() < p['HeadAimChance']
    decay = math.exp(-(1000/W['rec_ms'])*dt); interval = 60/W['rpm']
    delay = p['PerceptionDelay']*(1+ease); ae = max(1+2*ease, 0.5)
    my_speed = 0.0  # bot itself stands still in this duel (strafing is the target's)
    while t < max_t:
        nxt -= dt
        if strafe:
            if nxt <= 0: dirn = -dirn; nxt = rng.uniform(0.35,1.0)
            ty += dirn*spd*dt
        tv = dirn*spd if strafe else 0.0
        hist.append((t, ty))
        # position noise (OU) as UpdatePositionNoise
        amp = p['PositionNoise']*(1+2*max(ease,0)); tau=0.4; k = amp*math.sqrt(2*dt/tau)
        posn[0] += (-posn[0]/tau)*dt + k*rng.gauss(0,1)
        # PerceivedAimPoint: sample at now-delay, + vel*lead*delay
        idx = max(0, int(len(hist)-1-delay/dt)); seen = hist[idx][1] + tv*p['Lead']*delay + posn[0]
        wy = math.atan2(seen, dist)
        drop_chest = 0.0; head_off = 0.38 if aimhead else 0.0   # head point ~0.38 above chest aim (0.5 vs 0.12 drop)
        wp = math.atan2(head_off, dist)
        engaged = t >= react
        if engaged:
            wy -= p['RecoilCompensation']*recy; wp -= p['RecoilCompensation']*recp
        noise = max((1+2*ease)*(1+p['MoveAimPenalty']*my_speed/7.2)*1.0*(1.0 if engaged else 0.35), 0.2)
        if engaged:
            dy, dp = aim.step(dt, wy, wp, yaw, pitch, p, True, noise, ae)
            yaw += dy; pitch += dp
        recy *= decay; recp *= decay
        if engaged:
            cross_y = yaw + recy; cross_p = pitch + recp
            to_y = wy + p['RecoilCompensation']*recy  # bot believes target at want (before comp)
            perceived_err = math.hypot(math.atan2(seen, dist) - cross_y, math.atan2(head_off, dist) - cross_p)
            radius = math.atan2(0.28, max(dist, 1.0))
            cone = radius*p['FireCone']*(1+ease)
            gate = perceived_err < cone and not aim.flicking
            cool -= dt
            if pause > 0: pause -= dt
            if not (pause > 0 and burst <= 0):
                if burst <= 0 and gate:
                    burst = rng.randint(int(p['BurstMin']), int(p['BurstMax']))
                    if level != 'Easy' and dist > 28: burst = min(burst, rng.randint(1,3))
                if burst > 0 and (gate or level == 'Easy') and cool <= 0:
                    shots += 1; first = t if first is None else first; cool = interval; burst -= 1
                    sr = math.tan(W['spread']*D2R)*math.sqrt(rng.random()); sa = rng.uniform(0, math.tau)
                    cy_ = yaw + recy + sr*math.cos(sa); cp_ = pitch + recp + sr*math.sin(sa)
                    mh = dist*math.tan(cy_) - ty; mv = dist*math.tan(cp_)
                    head = (mh**2 + (mv-0.35)**2) <= 0.25**2; body = abs(mh) < 0.35 and -1.25 < mv < 0.55
                    if head: hits += 1; dmg += W['head']
                    elif body: hits += 1; dmg += W['body']
                    recp += W['rec_v']*D2R; recy += rng.uniform(-W['rec_h'], W['rec_h'])*D2R
                    if burst <= 0: pause = rng.uniform(p['BurstPauseMin'], p['BurstPauseMax'])
                    if dmg >= 100: return t, first, shots, hits
        t += dt
    return None, first, shots, hits

def run(level, dist, strafe, off, n=500, ease=0.0, seed=7):
    rng = random.Random(seed); ttk=[]; fail=0; shots=hits=0
    for _ in range(n):
        r = duel(level, dist, strafe, off, rng, ease)
        if r[0] is None: fail += 1
        else: ttk.append(r[0])
        shots += r[2]; hits += r[3]
    med = statistics.median(ttk) if ttk else float('nan')
    return med, hits/max(shots,1), fail/n

print("parsed Advanced.FlickAccelDeg =", P['Advanced']['FlickAccelDeg'], "| CounterStrafe =", P['Advanced']['CounterStrafe'])
print(f"{'level':9}{'dist':>5}{'target':>8}{'off':>5} | {'medTTK':>7} {'hit%':>6} {'noKill%':>8}")
for lv in ('Easy','Normal','Advanced'):
    for dist in (8, 18, 32):
        for strafe in (False, True):
            med, hr, fl = run(lv, dist, strafe, 70 if strafe else 0)
            print(f"{lv:9}{dist:5d}{'strafe' if strafe else 'stand':>8}{70 if strafe else 0:5d} | {med:7.2f} {100*hr:6.1f} {100*fl:8.1f}")
    print()
