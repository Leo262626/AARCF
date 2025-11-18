<script setup lang="ts">
import { ref } from 'vue';
import SideBar from '../common/SideBar.vue';
import { useEnvStore } from '@/models/stores/envStore';
import { storeToRefs } from 'pinia';
import { usePointLinkStore } from '@/models/stores/pointLinkStore';
import { ControlPointLinkType } from '@/models/save';
import { useSaveStore } from '@/models/stores/saveStore';
import { useStaClusterStore } from '@/models/stores/saveDerived/staClusterStore';
import { ControlPointSta } from '@/models/save';
import { useStaNameRectStore } from '@/models/stores/saveDerived/staNameRectStore';
import { useFormalizedLineStore } from '@/models/stores/saveDerived/formalizedLineStore';
import { useNameEditStore } from '@/models/stores/nameEditStore';

const envStore = useEnvStore()
const saveStore = useSaveStore()
const staClusterStore = useStaClusterStore()
const { setStaNameRect } = useStaNameRectStore()
const { setLinesFormalPts } = useFormalizedLineStore()
const nameEditStore = useNameEditStore()
const pointLinkStore = usePointLinkStore()
const { creatingLinkType } = storeToRefs(pointLinkStore)

function fd(){
    sidebar.value?.fold()
}

const sidebar = ref<InstanceType<typeof SideBar>>()
defineExpose({
    comeOut: ()=>{sidebar.value?.extend()},
    fold: ()=>{sidebar.value?.fold()}
})

function doJingBiao(){
    if(!saveStore.save) return

    const ordinaryClusters = staClusterStore.getStaClusters() || []
    const linkClusters = pointLinkStore.getClusterLinksPts() || []
    const clusteredIdSet = new Set<number>()
    const clusterReps = new Set<number>()
    for(const c of ordinaryClusters){
        if(!c || c.length<=1) continue
        let rep = c[0]
        for(const p of c){
            if(p.pos[0] < rep.pos[0] || (p.pos[0] === rep.pos[0] && p.pos[1] < rep.pos[1])){
                rep = p
            }
        }
        clusterReps.add(rep.id)
        for(const p of c) clusteredIdSet.add(p.id)
    }
    for(const c of linkClusters){
        if(!c || c.length<=1) continue
        let minId = c[0].id
        for(const p of c){ if(p.id < minId) minId = p.id }
        clusterReps.add(minId)
        for(const p of c) clusteredIdSet.add(p.id)
    }
    const targets: {id:number, pt:any}[] = []
    for(const pt of saveStore.save.points.filter((p:any)=>p.sta === ControlPointSta.sta)){
        const name = (pt.name || '').toString().trim()
        const nameS = (pt.nameS || '').toString().trim()
        const isUnnamed = name === '' && nameS === ''
        if(clusterReps.has(pt.id)){
            if(isUnnamed) targets.push({id:pt.id, pt})
        }else if(!clusteredIdSet.has(pt.id)){
            if(isUnnamed) targets.push({id:pt.id, pt})
        }
    }
    targets.sort((a,b)=>a.id - b.id)
    console.log('[doJingBiao] found targets count:', targets.length)
    console.log('[doJingBiao] targets:', targets.map(t=>({id:t.id, pos: t.pt.pos, name:t.pt.name, nameS:t.pt.nameS})))
    const existingNames = new Set<string>()
    for(const p of saveStore.save?.points || []){
        const n = (p.name || '').toString().trim()
        const ns = (p.nameS || '').toString().trim()
        if(n) existingNames.add(n)
        if(ns) existingNames.add(ns)
    }
    let idx = 1
    const affectedLineIds = new Set<number>()
    for(const t of targets){
        let newName = `-${idx}-`
        while(existingNames.has(newName)){
            idx++
            newName = `-${idx}-`
        }
        t.pt.name = newName
        if(!t.pt.nameP){
            try{ t.pt.nameP = nameEditStore.newNamePos(t.pt.id) }catch(e){}
        }
        existingNames.add(newName)
        try{ setStaNameRect(t.pt.id, undefined) }catch(e){}
        try{
            const lines = saveStore.getLinesByPt(t.pt.id) || []
            for(const l of lines) affectedLineIds.add(l.id)
        }catch(e){}
        idx++
    }
    for(const lid of affectedLineIds){
        try{ setLinesFormalPts(lid, undefined) }catch(e){}
    }

    if(targets.length === 0){
        const unnamed = (saveStore.save?.points||[]).filter((p:any)=>p.sta===ControlPointSta.sta && (!(p.name||'').toString().trim()) && (!(p.nameS||'').toString().trim()))
        console.log('[doJingBiao] no targets - unnamed stations total:', unnamed.length, unnamed.map(p=>({id:p.id,pos:p.pos})))
        console.log('[doJingBiao] ordinaryClusters:', ordinaryClusters.map(c=>c.map(p=>p.id)))
        console.log('[doJingBiao] linkClusters:', linkClusters.map(c=>c.map(p=>p.id)))
        alert('未发现可标注的未命名车站（已在控制台打印详细信息）')
        return
    }
    try{ envStore.rerender() }catch(e){/* best-effort */}
}
</script>

<template>
<SideBar ref="sidebar">
    <div class="toolItem">
        <div class="smallNote">用于添加标题/作者等信息，请勿用于标注站名</div>
        <div class="smallNote">需要线路/地形名称标签，请点击线路/地形创建</div>
        <div class="smallNoteVital">拖动到屏幕左上角即可删除</div>
        <button @click="envStore.createTextTag();fd()">创建文本标签</button>
    </div>
    <div class="toolItem">
        <div v-if="creatingLinkType===ControlPointLinkType.cluster" class="smallNote">
            选择“车站团”模式时，连线中央会有标记用细线，其仅在编辑器可见，导出时会隐藏
        </div>
        <select v-model="creatingLinkType">
            <option :value="ControlPointLinkType.fat">粗线</option>
            <option :value="ControlPointLinkType.thin">细线</option>
            <option :value="ControlPointLinkType.dot">虚线(原色)</option>
            <option :value="ControlPointLinkType.dotCover">虚线(覆盖)</option>
            <option :value="ControlPointLinkType.cluster">车站团</option>
        </select>
        <button @click="envStore.startCreatingPtLink();fd()">创建车站间连线</button>
    </div>
    <div class="toolItem">
        <div class="smallNote">可用于创建带折角/分叉的车站间连线</div>
        <button @click="envStore.createPlainPt();fd()">创建控制点</button>
    </div>
    <div class="toolItem">
        <button class="off">创建区间类型标记</button>
        <div class="smallNote">后续更新，敬请期待</div>
    </div>
    <div class="toolItem">
        <button @click="doJingBiao();fd()">竞标标记</button>
        <div class="smallNote">点击后，独立车站将被依次标注 "-1-","-2-"…</div>
    </div>
</SideBar>
</template>

<style scoped lang="scss">
.toolItem{
    display: flex;
    flex-direction: column;
    gap: 8px;
    border-bottom: 1px solid #aaa;
    align-items: center;
    padding: 12px;
}
</style>