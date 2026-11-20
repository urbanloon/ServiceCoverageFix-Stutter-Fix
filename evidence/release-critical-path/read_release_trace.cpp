// Decoder for the known, uncompressed x64 kernel records in the two release captures.
// Outputs scratch-only records, including thread IDs. Publish only the summary CSVs.
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

using Bytes = const unsigned char*;
using Tick = uint64_t;

template<typename T> T read_value(Bytes p) { T value; std::memcpy(&value, p, sizeof value); return value; }
std::string utf16_ascii(Bytes first, Bytes end) {
    std::string s;
    while (first + 1 < end && read_value<uint16_t>(first)) {
        s += first[1] == 0 ? char(first[0]) : '?';
        first += 2;
    }
    return s;
}

struct Trace {
    Bytes bytes; size_t length;
    explicit Trace(const char* path) {
        int fd = open(path, O_RDONLY);
        struct stat st{};
        if (fd < 0 || fstat(fd, &st)) throw std::runtime_error("Open ETL failed");
        length = st.st_size;
        if (length < 72) throw std::runtime_error("ETL header missing");
        bytes = (Bytes)mmap(nullptr,length,PROT_READ,MAP_PRIVATE,fd,0);
        close(fd);
        if (bytes == MAP_FAILED) throw std::runtime_error("Mapping ETL failed");
    }
    template<typename Fn> void scan(Fn process) {
        size_t offset = 0;
        while (offset < length) {
            if (length - offset < 72) throw std::runtime_error("Truncated buffer header");
            Bytes buf = bytes + offset;
            size_t size = read_value<uint32_t>(buf);
            if (size < 72 || size > length - offset || (read_value<uint16_t>(buf+52) & 0x40))
                throw std::runtime_error("Unsupported ETL buffer");
            size_t end = std::min<size_t>(size,read_value<uint32_t>(buf+48));
            unsigned cpu = read_value<uint16_t>(buf+40);
            for (size_t i = 72; i + 8 <= end;) {
                Bytes p = buf+i;
                if (read_value<uint32_t>(p) == UINT32_MAX) break;
                unsigned h = p[2];
                bool syst = (h >= 1 && h <= 4) || h == 16 || h == 17;
                size_t n = read_value<uint16_t>(p+(syst ? 4 : 0));
                if (n < 8 || i+n > end) throw std::runtime_error("Invalid ETL record");
                if (h == 0x14) process(p,p+n,nullptr,0,cpu);
                if (h == 17 || h == 2 || h == 4) {
                    size_t header = (h == 17 ? 16 : h == 2 ? 32 : 24) + ((p[1]&7)+(p[1]>>7))*8;
                    if (header+8 <= n) process(p,p+n,p+header,read_value<Tick>(p+(h==17?8:16)),cpu);
                }
                i += (n+7) & ~size_t(7);
            }
            offset += size;
        }
    }
};

struct Image {
    Tick base=0,size=0;
    bool contains(Tick ip) const {return ip-base < size;}
};

struct Thread {Tick start=0;};
struct Switch {Tick t; uint32_t next,old; uint8_t reason,state;};
struct Ready {Tick t; unsigned cpu,source; Tick in=0; unsigned context=0;};
struct Sample {
    Tick t,ip; unsigned cpu;
    uint32_t root=0, first_caller=0;
    int branch=0;
    bool joined=false;
};
struct Mark {unsigned tid; char event;};
int main(int argc,char**argv) {
    if (argc != 3) { std::fprintf(stderr,"Usage: read_release_trace INPUT.etl PRIVATE_PREFIX\n"); return 2; }
    Trace trace(argv[1]); std::string prefix=argv[2];
    std::map<uint32_t,std::map<std::string,Image>> images;
    const unsigned char image_id[] = {0xd7,0x75,0xe6,0xb3,0x54,0x25,0x18,0x4f,0x83,0x0b,0x27,0x62,0x73,0x25,0x60,0xde};
    trace.scan([&](Bytes p,Bytes end,Bytes,Tick,unsigned){
        if(p[2]==0x14 && p+76<=end && !std::memcmp(p+24,image_id,sizeof image_id)){
            std::string name=utf16_ascii(p+72,end);
            images[read_value<uint32_t>(p+64)][name]={read_value<Tick>(p+48),read_value<Tick>(p+56)};
        }
    });
    unsigned pid=0;
    for(auto& [id,mods]:images) if(mods.count("Cities2.exe") && mods.count("lib_burst_generated.dll")){
        if(pid) throw std::runtime_error("Multiple game processes");
        pid=id;
    }
    if(!pid) throw std::runtime_error("Missing game process");
    auto& mods=images.at(pid);
    Image exe=mods.at("Cities2.exe"), game=mods.at("lib_burst_generated.dll"),
          mod=mods.at("ServiceCoverageFix_win_x86_64.dll"), unity=mods.at("UnityPlayer.dll");
    std::map<unsigned,Thread> threads;
    trace.scan([&](Bytes p,Bytes end,Bytes d,Tick,unsigned){
        if(!d||p[7]!=5||read_value<unsigned>(d)!=pid) return;
        unsigned op=p[6], tid=read_value<unsigned>(d+4);
        if((op==1||op==3||op==4)&&d+64<=end) {
            threads[tid].start=read_value<Tick>(d+48);
        }
    });
    unsigned main_tid=0;
    for(auto& [tid,th]:threads) if(exe.contains(th.start)){
        if(main_tid && main_tid!=tid) throw std::runtime_error("Ambiguous entry thread");
        main_tid=tid;
    }
    if(!main_tid) throw std::runtime_error("No main thread");
    std::ofstream tf(prefix+".threads");
    for(auto& item:threads) tf<<item.first<<'\t'<<(item.first==main_tid?"entry":"other")<<'\n';
    std::map<unsigned,std::vector<Switch>> schedule;
    std::map<unsigned,std::vector<Sample>> samples;
    std::vector<Ready> readies;
    std::multimap<Tick,Mark> marks;
    std::ofstream changes(prefix+".changes");
    trace.scan([&](Bytes p,Bytes end,Bytes d,Tick t,unsigned cpu){
        if(!d)return;
        unsigned op=p[6],grp=p[7];
        if(grp==5 && op==36 && d+24<=end) {
            Switch s{t,read_value<unsigned>(d),read_value<unsigned>(d+4),d[12],d[14]};
            schedule[cpu].push_back(s);
            if(s.next==main_tid||s.old==main_tid) changes<<t<<' '<<(s.next==main_tid?"in":"out")<<' '<<int(s.reason)<<' '<<int(s.state)<<'\n';
            if(s.old==main_tid) marks.emplace(t,Mark{main_tid,'C'});
        }
        if(grp==5 && op==50 && read_value<unsigned>(d)==main_tid) {
            unsigned source=p[2]==17 ? 0 : read_value<unsigned>(p+8);
            readies.push_back({t,cpu,source});
        }
        if(grp==15 && op==46 && d+12<=end) {
            unsigned tid=read_value<unsigned>(d+8);
            if(threads.count(tid)) samples[tid].push_back({t,read_value<Tick>(d),cpu});
        }
    });
    for(auto& [cpu,sw]:schedule) std::sort(sw.begin(),sw.end(),[](auto a,auto b){return a.t<b.t;});
    std::ofstream rf(prefix+".ready");
    for(auto& r:readies){
        auto& sw=schedule.at(r.cpu);
        auto it=std::upper_bound(sw.begin(),sw.end(),r.t,[](Tick t,const Switch& s){return t<s.t;});
        if(it!=sw.begin()){
            --it; r.context=it->next; r.in=it->t;
        }
        if(!r.source) r.source=r.context;
        // A named header thread that disagrees with the running CPU invalidates a recent-sample join.
        if(r.context!=r.source) r.in=0;
        rf<<r.t<<' '<<r.cpu<<' '<<r.source<<' '<<r.in<<'\n';
        marks.emplace(r.t,Mark{r.source,'R'});
    }
    for(auto& [tid,sp]:samples) std::sort(sp.begin(),sp.end(),[](auto a,auto b){return a.t<b.t;});
    std::ofstream stacks(prefix+".stacks");
    trace.scan([&](Bytes p,Bytes end,Bytes d,Tick,unsigned){
        if(!d||p[6]!=32||p[7]!=24||d+24>end||read_value<unsigned>(d+8)!=pid)return;
        unsigned tid=read_value<unsigned>(d+12); Tick t=read_value<Tick>(d),leaf=read_value<Tick>(d+16);
        auto it=samples.find(tid); if(it==samples.end())return;
        auto& s=it->second;
        auto pos=std::lower_bound(s.begin(),s.end(),t,[](const Sample& a,Tick time){return a.t<time;});
        bool exact=pos!=s.end() && pos->t==t && pos->ip==leaf;
        auto m=marks.equal_range(t);
        if(!exact && m.first==m.second) return;
        uint32_t root=0, first_caller=0; int branch=0; bool has_unity=false; unsigned unity_frame=0;
        for(Bytes q=d+16;q+8<=end;q+=8) {
            Tick ip=read_value<Tick>(q);
            if(unity.contains(ip)){has_unity=true;if(!unity_frame)unity_frame=ip-unity.base;}
            if(mod.contains(ip)){root=0x80000000u+uint32_t(ip-mod.base); continue;}
            if(!game.contains(ip))continue;
            root=uint32_t(ip-game.base);
            if(q==d+24) first_caller=root;
            if(q==d+16) continue;  // Branch return addresses apply only to caller frames.
            switch(root){
                case 0xa20637: case 0xa20642: case 0xa2064a: case 0xa2065a: case 0xa20662: branch=1;break;
                case 0xa2055f: branch=2;break;
                case 0xa20451: branch=3;break;
            }
        }
        if(root==0x35f1767 && !branch && game.contains(leaf)){
            Tick l=leaf-game.base;
            if((l>=0xa20460&&l<0xa204b0)||(l>=0xa20572&&l<0xa2066d))branch=1;
        }
        if(exact) {pos->root=root; pos->branch=branch; pos->first_caller=first_caller; pos->joined=true;}
        for(auto j=m.first;j!=m.second;++j) if(j->second.tid==tid)
            stacks<<t<<' '<<j->second.event<<' '<<has_unity<<' '<<unity_frame<<' '<<root<<' '<<branch<<'\n';
    });
    std::ofstream output(prefix+".samples",std::ios::binary);
    for(auto& [tid,sp]:samples) for(auto& s:sp){
        // Express sample leaves as RVAs or as an external-image sentinel.
        uint32_t leaf=game.contains(s.ip)?uint32_t(s.ip-game.base):mod.contains(s.ip)?0x80000000u+uint32_t(s.ip-mod.base):UINT32_MAX;
        uint32_t data[]={tid,s.cpu,leaf,s.root,uint32_t(s.branch),s.joined?1u:0u,s.first_caller};
        output.write((char*)&s.t,sizeof s.t); output.write((char*)data,sizeof data);
    }
}
