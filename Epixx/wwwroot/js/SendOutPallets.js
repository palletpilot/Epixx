document.addEventListener("DOMContentLoaded", function () {
    const today = new Date().toISOString().split("T")[0];
    const dispatchBtn = document.querySelector(".dispatch-btn");
    const datePicker = document.getElementById("selected-date");
    const selectedTypeMission = document.getElementById("selected-type-mission");
    if (datePicker) {
        datePicker.value = today;
        datePicker.addEventListener("change", async function () {
            await updateUIBasedUponDateOrMissionChange();
        });

        selectedTypeMission.addEventListener("change", async function () {
            await updateUIBasedUponDateOrMissionChange();
        });
        dispatchBtn.addEventListener("click", async function () {
            await dispatchPalletOrders();
        })
    }
        
    async function dispatchPalletOrders() {
        const transfers = [];
        const rows = document.querySelectorAll(".pallet-row-1");

        if (rows.length === 0) {
            showNotification("Inga pallar att flytta!", "warning");
            return;
        }

        rows.forEach(row => {
            transfers.push({
                description: row.querySelector(".pallet-desc-1").textContent.trim(),
                count: Number(row.querySelector(".pallet-count-1").textContent),
                storeName: row.querySelector(".pallet-store-1")
                    ?.textContent.trim() ?? null
            });
        });

        const confirmed = await showConfirm(
            `Är du säker på att du vill skicka ut dessa uppdrag för dag ${datePicker.value}?`,
            "Skicka uppdrag"
        );

        if (!confirmed) {
            return;
        }

        const response = await fetch(
            `/Admin/DispatchPalletOrders?type=${encodeURIComponent(selectedTypeMission.value)}`,
            {
                method: "POST",
                headers: {
                    "Content-Type": "application/json"
                },
                body: JSON.stringify(transfers)
            }
        );

        if (!response.ok) {
            showNotification(
                "Något gick fel när uppdragen skulle skickas.",
                "error"
            );
            return;
        }

        showNotification("Uppdrag skickades ut!", "success");

        await updateUIBasedUponDateOrMissionChange();
    }
    async function updateUIBasedUponDateOrMissionChange() {
        const response = await fetch(
            '/Admin/FetchPalletsBasedUponDateAndMission?mission='
            + encodeURIComponent(selectedTypeMission.value)
            + '&date='
            + encodeURIComponent(datePicker.value)
        );

        if (!response.ok) {
            console.error("Error fetching pallets");
            return;
        }

        const data = await response.json();
        const tbody = document.getElementById("tbody-for-day");
        const storeTh = document.getElementById("store-th");
        // Viktigt - rensa gamla rader
        tbody.innerHTML = "";

        const showStore =
            selectedTypeMission.value === "Awaiting PackingAreaTransfer";

        if (showStore) {
            storeTh.classList.remove("hidden");
        } else {
            storeTh.classList.add("hidden");
        }
        if (data.length == 0) {
            const row = document.createElement("tr");
            const emptyTD = document.createElement("td");
            if (showStore) {
                emptyTD.textContent = "Inga omflytt till packyta har beordrats än!"
            } else {
                emptyTD.textContent = "Inga omflytt till plock har beordrats än!";
            }
            row.appendChild(emptyTD);
            tbody.appendChild(row);
            return;
            
        }
        data.forEach(item => {
            const row = document.createElement("tr");
            row.classList.add("pallet-row-1");
            const descriptionTd = document.createElement("td");
            descriptionTd.textContent = item.description;
            descriptionTd.classList.add("pallet-desc-1");
            row.appendChild(descriptionTd);

            const countTd = document.createElement("td");
            countTd.textContent = item.count;
            countTd.classList.add("pallet-count-1");
            row.appendChild(countTd);

            if (showStore) {
                const storeTd = document.createElement("td");
                storeTd.textContent = item.storeName ?? "-";
                storeTd.classList.add("pallet-store-1");
                row.appendChild(storeTd);
            }

            tbody.appendChild(row);
        });
    }

   
    if (selectedTypeMission)
        updateUIBasedUponDateOrMissionChange();
});