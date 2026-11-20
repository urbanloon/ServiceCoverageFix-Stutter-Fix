using System;
using Game.Net;
using Game.Pathfind;
using Game.Common;
using Unity.Collections;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using ECS = Unity.Entities.EntityComponentStore;
[BurstCompile]
public static unsafe class Probe
{
    public struct Descriptor
    {
        public IntPtr Memory, Inputs, Outputs;
        public int ArenaBytes, MaxEntities, Length, Archetypes, Mask, Roads, Capacity, StartLength, ProviderVersion, StatusVersion;
    }
    static byte* Take(ref byte* next,int size) {
        byte* p=next;
        next+=(size+63)&~63;
        return p;
    }
    [BurstCompile(FloatMode=FloatMode.Strict)]
    public static int Run(IntPtr argument,int action)
    {
        Descriptor p=*(Descriptor*)argument;
        byte* start=(byte*)p.Memory;
        byte* next=start;
        var access=(EntityDataAccess*)Take(ref next,sizeof(EntityDataAccess));
        var arches=(Archetype*)Take(ref next,sizeof(Archetype)*p.Archetypes);
        var lookup=new ComponentLookup<EdgeLane>();
        lookup.m_Access=access;
        lookup.m_TypeIndex=new TypeIndex{Value=7};
        if (action==0)
        {
            const int cap=256;
            int chunkCount=(p.MaxEntities+cap-1)/cap+2;
            var per=(ECS.PerChunkArray.PerChunkData*)Take(ref next,chunkCount*sizeof(ECS.PerChunkArray.PerChunkData));
            ECS.PerChunkArray.StaticIdentifier.Ref.Data.m_PerChunkData=per;
            for(int a=0;a<p.Archetypes;++a) {
                var ar=arches+a;
                ar->EntityComponentStore=&access->m_EntityComponentStore;
                ar->Types=(ComponentTypeInArchetype*)Take(ref next,4*sizeof(ComponentTypeInArchetype));
                ar->Chunks.m_Data=Take(ref next,256);ar->Chunks.Capacity=1;ar->Chunks.Count=1;ar->Chunks.ComponentCount=4;
                ar->TypesCount=4;
                ar->Types[0].TypeIndex=new TypeIndex {Value=1};
                ar->Types[1].TypeIndex=new TypeIndex{Value=3};
                ar->Types[2].TypeIndex=new TypeIndex {Value=(a&p.Mask)!=0?9:7};
                ar->Types[3].TypeIndex=new TypeIndex{Value=12};
                ar->Offsets=(int*)Take(ref next,4*sizeof(int));
                ar->SizeOfs=(ushort*)Take(ref next,4*sizeof(ushort));
                ar->SizeOfs[0]=8;ar->SizeOfs[1]=8;ar->Offsets[1]=cap*8;ar->SizeOfs[2]=8;ar->Offsets[2]=cap*16;ar->SizeOfs[3]=16;ar->Offsets[3]=cap*24;
            }
            for(int m=0;m<(chunkCount+63)/64;++m) {
                long ptr=(long)Take(ref next,64*16384);
                ECS.s_chunkStore.Data.m_megachunk.ElementAt(m)=ptr;
            }
            for (int c=1;c<chunkCount;++c) {
                per[c].Archetype=arches+(c%p.Archetypes);
            }
            for(int b=0;b<(p.MaxEntities+8191)/8192;++b){
                var block=(ECS.EntityStore.DataBlock*)Take(ref next,sizeof(ECS.EntityStore.DataBlock));
                ECS.s_entityStore.Data.m_DataBlocks[b]=(ulong)block;
            }
            for(int i=0;i<p.MaxEntities;++i) {
                int c=i/cap+1,j=i%cap;
                var ent=new Entity{Index=i,Version=1};
                var block=(ECS.EntityStore.DataBlock*)ECS.s_entityStore.Data.m_DataBlocks[i/8192];
                block->allocated[(i%8192)/64]|=(1ul<<(i%64));
                ECS.s_entityStore.Data.SetEntityInChunk(ent,new EntityInChunk{Chunk=new ChunkIndex{Value=c},IndexInChunk=j});
                ECS.s_entityStore.Data.SetEntityVersion(ent,1);
                byte* chunk=(byte*)ECS.s_chunkStore.Data.GetChunkPointer(c);
                ((Entity*)(chunk+64+cap*8))[j]=new Entity {Index=i%p.Roads+19,Version=3};
                ((float2*)(chunk+64+cap*16))[j]=i%3==0?new float2(0,1):i%3==1?new float2(1,0):new float2(0.5f,1);
            }
            return (int)(next-start);
        }
        byte* providerChunk=(byte*)ECS.s_chunkStore.Data.GetChunkPointer(1);
        var header=(BufferHeader*)(providerChunk+64+256*24);
        header->Pointer=(byte*)p.Outputs;header->Length=p.StartLength;header->Capacity=p.Capacity;
        var versions=(arches+(1%p.Archetypes))->Chunks.GetChangeVersionArrayForType(3);
        *versions=123;
        var ownerLookup=new ComponentLookup<Owner>();
        ownerLookup.m_Access=access;ownerLookup.m_TypeIndex=new TypeIndex{Value=3};
        var targets = new BufferLookup<CoverageElement>();
        targets.m_Access=access;targets.m_TypeIndex=new TypeIndex{Value=12};targets.m_GlobalSystemVersion=561;
        var list=new NativeList<CoverageJobs.ResultItem>(1,Allocator.Temp);
        var provider=new Entity{Index=0,Version=p.ProviderVersion};
        var resultList=new UnsafeList<CoverageResult>((CoverageResult*)p.Inputs,p.Length);
        list.Add(new CoverageJobs.ResultItem{m_Owner=provider,m_Results=resultList});
        if(action==1) {
            var job=new CoverageJobs.ProcessResultsJob{m_ResultItems=list,m_EdgeLaneData=lookup,m_OwnerData=ownerLookup,m_CoverageElements=targets};
            job.Execute(0);
        }else {
            var item=list[0];
            if(targets.HasBuffer(item.m_Owner)){
                var map=new NativeParallelHashMap<Entity,int>(100,Allocator.Temp);
                var dst=targets[item.m_Owner];dst.Clear();
                for(int j=0;j<item.m_Results.Length;++j){
                    var row=item.m_Results[j];
                    EdgeLane lane;
                    if(action==2) {
                        if(!lookup.HasComponent(row.m_Target))continue;
                        lane=lookup[row.m_Target];
                    }else if(!lookup.TryGetComponent(row.m_Target,out lane))continue;
                    Entity road=ownerLookup[row.m_Target].m_Owner;
                    float2 ends=new float2(0,1);
                    float2 cost=math.select(math.select(float.MaxValue,row.m_TargetCost.yx,lane.m_EdgeDelta.yx==ends),row.m_TargetCost,lane.m_EdgeDelta==ends);
                    if(map.TryGetValue(road,out int pos)) {
                        var elem=dst[pos];
                        if(math.any(cost<elem.m_Cost)){
                            elem.m_Cost=math.min(elem.m_Cost,cost);dst[pos]=elem;
                        }
                    }else{
                        map.TryAdd(road,dst.Length);
                        dst.Add(new CoverageElement{m_Edge=road,m_Cost=cost});
                    }
                }
                map.Dispose();
            }
        }
        list.Dispose();
        ((Descriptor*)argument)->StatusVersion=(int)*versions;
        return header->Length;
    }
}
