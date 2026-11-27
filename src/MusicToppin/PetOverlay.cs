using System.Text;

namespace MusicToppin;

/// <summary>
/// 开发分支原型：PNG 立绘 + 程序动画/粒子特效的宠物互动层。
/// 尾巴用 SVG clipPath 从同一张 PNG 裁出，放在本体下层摆动（本体不挖洞，避免漏底/断尾）。
/// 访问 /pets 使用；透明背景，可直接当浏览器源；?demo=1 按钮，?pet= 形象，?act= 动作。
/// </summary>
internal static class PetOverlay
{
    public const string Page = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<title>Music Toppin · Pet</title>
<style>
  html,body{margin:0;padding:0;width:100%;height:100%;background:transparent;overflow:hidden;font-family:'Microsoft YaHei UI',sans-serif;}
  #pet{position:absolute;left:50%;bottom:2%;transform:translateX(-50%);transform-origin:50% 92%;pointer-events:none;}
  #stage{position:relative;display:inline-block;transform-origin:50% 95%;
    filter:drop-shadow(0 8px 18px rgba(0,0,0,.35));}
  #baseImg,#tailImg{display:block;height:min(86vh,86vw);max-height:520px;width:auto;}
  #tailImg{position:absolute;left:0;top:0;width:100%;height:100%;z-index:0;}
  #baseImg{position:relative;z-index:1;}
  #pet.idle #stage{animation:breathe 3.8s ease-in-out infinite;}
  #pet.idle #tailImg{animation:wag 2.8s ease-in-out infinite;}
  #pet.tilt #stage{transform:rotate(-8deg);transition:transform .35s ease;}
  #pet.glow #stage{filter:drop-shadow(0 0 18px rgba(143,184,255,.95)) drop-shadow(0 8px 18px rgba(0,0,0,.35));}
  #pet.hop{animation:hop .5s cubic-bezier(.3,1.5,.4,1);}
  #pet.beat{animation:beat .45s ease-in-out;}
  #pet.shake{animation:shake .45s ease-in-out;}
  #pet.pounce{animation:pounce .55s cubic-bezier(.2,1.5,.4,1);}
  #pet.dead{animation:dead .8s ease forwards;transform-origin:50% 80%;}
  #pet.dead #stage{animation:none!important;}
  #pet.dead #tailImg{animation:none!important;}
  #pet.dead #baseImg,#pet.dead #tailImg{filter:grayscale(.4);}
  @keyframes breathe{0%,100%{transform:scale(1,1) translateY(0);}50%{transform:scale(1.012,1.018) translateY(-3px);}}
  @keyframes wag{0%,100%{transform:rotate(0deg);}50%{transform:rotate(-7deg);}}
  @keyframes hop{0%,100%{transform:translate(-50%,0);}40%{transform:translate(-50%,-26px) scale(1.02);}}
  @keyframes beat{0%,100%{transform:translate(-50%,0) scale(1);}45%{transform:translate(-50%,-10px) scale(1.02);}}
  @keyframes shake{0%,100%{transform:translate(-50%,0);}20%{transform:translate(-53%,0) rotate(-3deg);}60%{transform:translate(-47%,0) rotate(3deg);}}
  @keyframes pounce{0%{transform:translate(-50%,0) scale(1);}35%{transform:translate(-50%,-32px) scale(1.06) rotate(-5deg);}70%{transform:translate(-50%,6px) scale(.98);}100%{transform:translate(-50%,0);}}
  @keyframes dead{to{transform:translate(-50%,8px) rotate(22deg) scale(.92);opacity:.85;}}
  /* 尾巴裁剪：本体整图在上，尾巴在下层绕支点摆动，接缝被本体盖住 */
  #pet[data-theme=""siamese""] #tailImg{clip-path:url(#tail-siamese);transform-origin:77% 53%;}
  #pet[data-theme=""dachshund""] #tailImg{clip-path:url(#tail-dachshund);transform-origin:79% 44%;}
  #pet[data-theme=""devon""] #tailImg{clip-path:url(#tail-devon);transform-origin:80% 44%;}
  #sign{position:absolute;left:50%;bottom:100%;transform:translateX(-50%);background:rgba(14,17,23,.92);border:1px solid rgba(255,255,255,.18);
    border-radius:12px;padding:8px 14px;color:#eaf2ff;font-size:16px;white-space:nowrap;opacity:0;transition:opacity .2s;pointer-events:none;max-width:92%;overflow:hidden;text-overflow:ellipsis;z-index:5;}
  #sign.on{opacity:1;}
  #fx{position:absolute;inset:0;pointer-events:none;overflow:visible;z-index:4;}
  .heart,.star,.confetti{position:absolute;font-size:22px;animation:floatUp 1.6s ease-out forwards;}
  @keyframes floatUp{0%{transform:translateY(0) scale(.7);opacity:0}20%{opacity:1}100%{transform:translateY(-160px) scale(1.1);opacity:0}}
  #crown{position:absolute;left:50%;top:-2%;transform:translateX(-50%);font-size:36px;opacity:0;transition:opacity .2s;z-index:5;}
  #crown.on{opacity:1;}
  #demo{position:absolute;left:8px;top:8px;display:flex;flex-wrap:wrap;gap:6px;max-width:92%;z-index:9;}
  #demo button{font-size:12px;padding:4px 8px;border-radius:6px;border:1px solid rgba(255,255,255,.25);background:rgba(14,17,23,.8);color:#eaf2ff;cursor:pointer;}
  #demo .sep{width:100%;height:0;}
  .hidden{display:none!important;}
</style>
</head>
<body>
<svg width=""0"" height=""0"" style=""position:absolute"" aria-hidden=""true"">
  <defs>
    <clipPath id=""tail-siamese"" clipPathUnits=""objectBoundingBox""><polygon points=""0.70,0.50 0.76,0.50 0.82,0.53 0.86,0.57 0.88,0.63 0.885,0.71 0.87,0.79 0.845,0.815 0.815,0.80 0.80,0.72 0.795,0.64 0.78,0.585 0.74,0.55""/></clipPath>
    <clipPath id=""tail-dachshund"" clipPathUnits=""objectBoundingBox""><polygon points=""0.73,0.41 0.79,0.41 0.85,0.45 0.885,0.50 0.90,0.56 0.905,0.64 0.90,0.70 0.885,0.745 0.855,0.745 0.835,0.68 0.825,0.60 0.795,0.53 0.755,0.47""/></clipPath>
    <clipPath id=""tail-devon"" clipPathUnits=""objectBoundingBox""><polygon points=""0.75,0.41 0.81,0.41 0.86,0.45 0.885,0.51 0.90,0.58 0.905,0.66 0.905,0.76 0.895,0.86 0.875,0.93 0.845,0.935 0.825,0.87 0.815,0.76 0.805,0.66 0.785,0.56 0.745,0.47""/></clipPath>
  </defs>
</svg>
<div id=""pet"" class=""idle"" data-theme=""siamese""><div id=""stage"">
  <img id=""tailImg"" alt="""" /><img id=""baseImg"" alt="""" />
  <div id=""sign""><span id=""signText"" style=""display:block;overflow:hidden;text-overflow:ellipsis;""></span></div>
  <div id=""crown"">👑</div>
  <div id=""fx""></div>
</div></div>
<div id=""demo"" class=""hidden"">
  <button onclick=""act('song','示例歌曲','测试观众')"">点歌</button>
  <button onclick=""act('beat')"">歌词/节拍</button>
  <button onclick=""act('gift',null,null,'示例观众 送出 小心心 ×3')"">礼物</button>
  <button onclick=""act('up')"">上舰</button>
  <button onclick=""act('hop')"">跳</button>
  <button onclick=""act('tilt')"">歪头</button>
  <button onclick=""act('shake')"">抖动</button>
  <button onclick=""act('pounce')"">扑击</button>
  <button onclick=""act('dead')"">装死</button>
  <button onclick=""act('glow')"">发光</button>
  <button onclick=""reset()"">重置</button>
  <div class=""sep""></div>
  <button onclick=""setPet('siamese')"">暹罗</button>
  <button onclick=""setPet('dachshund')"">腊肠</button>
  <button onclick=""setPet('devon')"">德文</button>
</div>
<script>
(function(){
  var pet=document.getElementById('pet');
  var stage=document.getElementById('stage');
  var baseImg=document.getElementById('baseImg');
  var tailImg=document.getElementById('tailImg');
  var sign=document.getElementById('sign');
  var signText=document.getElementById('signText');
  var fx=document.getElementById('fx');
  var crown=document.getElementById('crown');

  var themes={siamese:'/assets/themes/siamese.png?v=150',dachshund:'/assets/themes/dachshund.png?v=150',devon:'/assets/themes/devon.png?v=150'};
  var current='';

  var timers={};
  function once(cls, ms){
    pet.classList.remove(cls);
    void pet.offsetWidth;
    pet.classList.add(cls);
    if(timers[cls]) clearTimeout(timers[cls]);
    timers[cls]=setTimeout(function(){ pet.classList.remove(cls); delete timers[cls]; }, ms);
  }
  function spawn(symbols, count, cls){
    for(var i=0;i<count;i++){
      var el=document.createElement('div');
      el.className=cls||'heart';
      el.textContent=symbols[Math.floor(Math.random()*symbols.length)];
      el.style.left=(30+Math.random()*40)+'%';
      el.style.bottom=(34+Math.random()*26)+'%';
      el.style.animationDelay=(Math.random()*0.35)+'s';
      el.addEventListener('animationend', function(){ if(this.parentNode) this.parentNode.removeChild(this); });
      fx.appendChild(el);
    }
  }
  function showSign(text, ms){
    signText.textContent=text||'';
    sign.classList.add('on');
    setTimeout(function(){ sign.classList.remove('on'); }, ms||2600);
  }
  function setPet(name){
    if(!themes[name]) name='siamese';
    current=name;
    pet.setAttribute('data-theme', name);
    baseImg.src=themes[name];
    tailImg.src=themes[name];
  }
  function reset(){
    pet.className='idle';
    sign.classList.remove('on'); crown.classList.remove('on'); fx.innerHTML='';
  }
  function act(name, title, requester, detail){
    if(name!=='dead' && pet.classList.contains('dead')) pet.classList.remove('dead');
    if(name==='song'){
      showSign('点歌：'+(title||'')+(requester?(' — '+requester):''),3200);
      pet.classList.add('tilt'); setTimeout(function(){ pet.classList.remove('tilt'); }, 1800);
      spawn(['🎵','🎶'],3,'star');
    }else if(name==='beat'){ if(pet.classList.contains('dead')) return; once('beat',500); }
    else if(name==='gift'){
      once('hop',500);
      if(detail) showSign(detail,2600);
      spawn(['❤️','💛','✨','💫'],10,'heart');
    }
    else if(name==='up'){ once('hop',600); crown.classList.add('on'); setTimeout(function(){ crown.classList.remove('on'); },2600); showSign(detail||'有人上舰',2600); spawn(['🎉','✨','💖'],16,'confetti'); }
    else if(name==='hop'){ once('hop',500); }
    else if(name==='tilt'){ pet.classList.add('tilt'); setTimeout(function(){ pet.classList.remove('tilt'); },1800); }
    else if(name==='shake'){ once('shake',500); }
    else if(name==='pounce'){ once('pounce',600); }
    else if(name==='dead'){ pet.classList.add('dead'); setTimeout(function(){ pet.classList.remove('dead'); },4000); }
    else if(name==='glow'){ once('glow',1400); }
  }
  window.act=act; window.reset=reset; window.setPet=setPet;

  baseImg.onerror=function(){ pet.style.visibility='hidden'; };
  tailImg.onerror=function(){ tailImg.style.visibility='hidden'; };

  var params=new URLSearchParams(location.search);
  var forced=params.get('pet');
  if(forced!=='siamese'&&forced!=='dachshund'&&forced!=='devon') forced='';
  setPet(forced||'siamese');
  if(!(window.CSS && CSS.supports && CSS.supports('clip-path','url(#x)'))){ tailImg.style.display='none'; }

  // 跟随应用主题、播放事件、真实礼物/上舰
  var lastTitle='', lastVersion=-1, lastToast=0, lastTheme='', lastPetEvent=0, baseDone=false;
  function fetchJson(url){
    var ctrl = window.AbortController ? new AbortController() : null;
    var timer = ctrl ? setTimeout(function(){ ctrl.abort(); }, 2500) : null;
    var opts = ctrl ? { signal: ctrl.signal } : {};
    return fetch(url, opts).then(function(r){ return r.json(); }).then(function(d){
      if(timer) clearTimeout(timer);
      return d;
    }, function(e){
      if(timer) clearTimeout(timer);
      throw e;
    });
  }
  function poll(){
    fetchJson('/lyrics/state?t='+Date.now()).then(function(st){
      var theme=st.theme||'';
      if(!forced && theme && theme!==lastTheme && themes[theme]){ lastTheme=theme; setPet(theme); }
      var title=st.title||'', v=Number(st.version);
      if(!baseDone){
        // 首次只建基线：避免页面重载时重放当前歌/历史提示
        baseDone=true;
        lastTitle=title; lastVersion=isFinite(v)?v:-1; lastToast=Number(st.toastId)||0;
      }else{
        if(title && title!==lastTitle){ lastTitle=title; act('song', title, st.requester||''); }
        if(isFinite(v) && v!==lastVersion){ if(lastVersion>=0) act('beat'); lastVersion=v; }
        if(st.toastId && st.toastId!==lastToast){ lastToast=st.toastId; }
      }
      // 真实礼物 / 上舰事件：只播放 5 秒内的新事件，页面重载不重放历史
      var pe=Number(st.petEventId)||0;
      if(pe && pe!==lastPetEvent){
        lastPetEvent=pe;
        var at=Date.parse(st.petEventAt||'');
        if(isFinite(at) && Date.now()-at < 5000){
          act(st.petEvent==='up'?'up':'gift', null, null, st.petEventText||'');
        }
      }
      pet.classList.toggle('idle', !!st.playing);
    }).catch(function(){}).then(function(){ setTimeout(poll, 700); });
  }
  poll();

  if(params.get('demo')==='1'){ document.getElementById('demo').classList.remove('hidden'); }
  var actName=params.get('act');
  if(actName) setTimeout(function(){ act(actName); }, 700);
})();
</script>
</body>
</html>";
}
