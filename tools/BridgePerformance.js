(() => {
  const {count,mode}=window.scenario;
  const pieces=['<header><summary data-model-select="true"><span class="chat-controls__inline-select-label">gpt-5</span></summary><textarea></textarea><button aria-label="Stop">Stop</button></header><main role="log">'];
  for(let i=0;i<count;i++) pieces.push(`<article class="message assistant"><p>${'Synthetic long conversation paragraph. '.repeat(35)}</p><button aria-label="Copy message">Copy</button><details><summary>Tool output</summary><pre>${'Example output\n'.repeat(20)}</pre><button>Collapse</button></details></article>`);
  pieces.push('</main>');document.body.innerHTML=pieces.join('');
  if(mode==='legacy'){const app=document.createElement('openclaw-app');app.connected=true;app.currentModel='gpt-5';app.chatRunId='test-run';document.body.append(app);}
  window.measurements={inspectCount:0,inspectMs:0,style:0,rect:0,inner:0,queries:0};
  const style=window.getComputedStyle;window.getComputedStyle=function(...args){measurements.style++;return style.apply(this,args);};
  const rect=Element.prototype.getBoundingClientRect;Element.prototype.getBoundingClientRect=function(...args){measurements.rect++;return rect.apply(this,args);};
  const inner=Object.getOwnPropertyDescriptor(HTMLElement.prototype,'innerText');Object.defineProperty(HTMLElement.prototype,'innerText',{...inner,get(){measurements.inner++;return inner.get.call(this);}});
  const query=document.querySelectorAll.bind(document);document.querySelectorAll=function(...args){measurements.queries++;return query(...args);};
  const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
  window.runMeasurements=async()=>{
    await wait(300);
    const snapshot=()=>({...measurements});
    const difference=(a,b)=>Object.fromEntries(Object.keys(a).map(key=>[key,a[key]-b[key]]));
    const before=snapshot();const durations=[];
    for(let i=0;i<12;i++){
      document.body.style.paddingLeft=(i%2)+'px';
      const begin=performance.now();window.__openClawHostBridge.inspect();durations.push(performance.now()-begin);
    }
    const isolated=difference(snapshot(),before);
    const mutationBefore=snapshot();let mutations=0;
    const timer=setInterval(()=>{const badge=document.createElement('span');badge.textContent='update';document.querySelector('header').append(badge);badge.remove();mutations++;},50);
    await wait(2100);clearInterval(timer);
    const mutation=difference(snapshot(),mutationBefore);
    durations.sort((a,b)=>a-b);
    const result={count,mode,visibility:document.visibilityState,buttons:document.querySelectorAll('button').length,isolated,p50Ms:durations[6],p95Ms:durations[11],mutations,mutation};
    if(window.scenario.verifyBehavior&&count===100&&mode==='modern'){
      const inspect=()=>window.__openClawHostBridge.inspect();
      const beforeText=inspect();
      const latest=document.querySelector('main').lastElementChild.querySelector('p');
      latest.textContent=latest.textContent.slice(0,-6)+'ABCDEF';
      const afterText=inspect();
      latest.textContent=latest.textContent.slice(0,-6)+'UVWXYZ';
      const sameLengthEdit=inspect();
      const modelLabel=document.querySelector('.chat-controls__inline-select-label');
      modelLabel.textContent='claude-sonnet';
      const changedModel=inspect();
      const misleading=document.createElement('button');misleading.textContent='gpt-unrelated';document.body.append(misleading);
      const priorityModel=inspect();
      const stop=document.querySelector('button[aria-label="Stop"]');stop.remove();
      const log=document.querySelector('main');log.removeAttribute('role');
      await wait(1300);
      const latestStatus=(window.statusMessages||[]).filter(message=>message.kind==='harness-control-ui-status').at(-1);
      const flags={modelInitiallyCorrect:beforeText.currentModel==='gpt-5',latestOutputDetected:beforeText.activitySignature!==afterText.activitySignature,
        sameLengthEditDetected:afterText.activitySignature!==sameLengthEdit.activitySignature,
        modelChangeDetected:changedModel.currentModel==='claude-sonnet',trustedModelBeatsUnrelatedControl:priorityModel.currentModel==='claude-sonnet',
        finalMutationPublishedIdle:latestStatus?.isBusy===false};
      const status=document.createElement('div');status.setAttribute('role','status');
      status.innerHTML='Ready <span style="display:none">auth_token_missing</span><span aria-hidden="true">authentication required</span><details><summary>Reference</summary>token mismatch</details>';
      document.body.append(status);flags.hiddenErrorTextIgnored=inspect().phase==='connected';
      const alert=document.createElement('div');alert.setAttribute('role','alert');alert.textContent='auth_token_missing';document.body.append(alert);
      flags.visibleAuthErrorDetected=inspect().phase==='auth_required';alert.remove();status.remove();
      modelLabel.innerHTML='<span style="display:contents">claude-sonnet</span>';
      flags.displayContentsModelDetected=inspect().currentModel==='claude-sonnet';
      result.behavior=flags;
    }
    return result;
  };
})();
